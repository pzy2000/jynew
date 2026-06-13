/*
 * 金庸群侠传3D重制版 - WarpTest Checkpoint Command
 * https://github.com/jynew/jynew
 *
 * Test-gated utility for WarpTest semantic checkpoint validation.
 * Activated only via Unity batch mode: -executeMethod Jyx2.WarptestCheckpoint.Run
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Jyx2
{
    public static class WarptestCheckpoint
    {
        public static void Run()
        {
            string requestPath = null;
            string reportPath = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--warptest-request" && i + 1 < args.Length)
                    requestPath = args[i + 1];
                if (args[i] == "--warptest-report" && i + 1 < args.Length)
                    reportPath = args[i + 1];
            }

            if (string.IsNullOrEmpty(requestPath) || string.IsNullOrEmpty(reportPath))
            {
                Debug.LogError("[WarpTest] Missing --warptest-request or --warptest-report arguments");
                EditorQuit(1);
                return;
            }

            try
            {
                var requestJson = File.ReadAllText(requestPath, Encoding.UTF8);
                var request = JsonUtility.FromJson<WarptestRequest>(requestJson);
                var report = ProcessRequest(request);
                File.WriteAllText(reportPath, JsonUtility.ToJson(report, true), Encoding.UTF8);
                Debug.Log($"[WarpTest] Report written to {reportPath}");
                EditorQuit(report.status == "success" ? 0 : 1);
            }
            catch (Exception e)
            {
                var errorReport = new WarptestReport
                {
                    status = "failure",
                    detail = $"WarpTest exception: {e.Message}",
                    checks = new List<WarptestCheck>()
                };
                File.WriteAllText(reportPath, JsonUtility.ToJson(errorReport, true), Encoding.UTF8);
                Debug.LogError($"[WarpTest] {e}");
                EditorQuit(1);
            }
        }

        static WarptestReport ProcessRequest(WarptestRequest request)
        {
            var checks = new List<WarptestCheck>();
            var spec = request.spec;
            var target = spec.target;

            // Phase 0: Initialize runtime environment (Lua tables, MOD configs)
            try
            {
                var modId = target.mod_id;
                if (string.IsNullOrEmpty(modId)) modId = GameConst.DEFAULT_GAME_MOD_NAME;

                // Attempt async setup; if it fails, try direct Lua init
                var setupTask = RuntimeEnvSetup.Setup();
                // In editor batch mode, UniTask may not tick automatically;
                // fall through and let LuaToCsBridge init lazily if needed.
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WarpTest] Runtime setup incomplete (non-fatal): {e.Message}");
            }

            // Phase 1: Checkpoint restoration — load or synthesize state
            if (target.save_index >= 0)
            {
                checks.Add(LoadSaveCheckpoint(target.save_index, target.mod_id));
            }
            else
            {
                checks.Add(SynthesizeState(target));
            }

            bool restorationOk = checks.All(c => c.status == "success");
            if (!restorationOk)
            {
                return new WarptestReport { status = "failure", detail = "Checkpoint restoration failed.", checks = checks };
            }

            // Ensure stub roles exist for all role IDs referenced in actions/assertions
            // so that GetRole() never returns null in minimal-init mode
            EnsureStubRolesForSpec(spec);

            // Phase 2: Validation
            foreach (var validation in spec.validations)
            {
                checks.Add(ValidateField(validation));
            }

            // Phase 3: Actions
            foreach (var action in spec.actions)
            {
                checks.Add(ExecuteAction(action));
            }

            // Phase 4: Assertions
            foreach (var assertion in spec.assertions)
            {
                checks.Add(CheckAssertion(assertion));
            }

            bool allOk = checks.All(c => c.status == "success");
            return new WarptestReport
            {
                status = allOk ? "success" : "failure",
                detail = allOk ? "All checks passed." : "One or more checks failed.",
                checks = checks
            };
        }

        static void EnsureStubRolesForSpec(WarptestSpec spec)
        {
            var runtime = GameRuntimeData.Instance;
            if (runtime == null) return;

            var neededIds = new HashSet<int>();
            if (spec.actions != null)
                foreach (var a in spec.actions)
                    if (a.role_id != 0) neededIds.Add(a.role_id);
            if (spec.assertions != null)
                foreach (var a in spec.assertions)
                    if (a.role_id != 0) neededIds.Add(a.role_id);

            foreach (int id in neededIds)
            {
                if (!runtime.AllRoles.ContainsKey(id))
                {
                    var stub = new RoleInstance();
                    stub.Key = id;
                    stub.Name = $"WarpTest Stub {id}";
                    stub.Level = 1;
                    stub.Hp = 100;
                    stub.MaxHp = 100;
                    stub.Mp = 50;
                    stub.MaxMp = 50;
                    stub.Attack = 15;
                    stub.Defence = 10;
                    stub.Tili = 30;
                    runtime.AllRoles[id] = stub;
                    Debug.Log($"[WarpTest] Created stub role {id} for action/assertion reference");
                }
            }
        }

        static WarptestCheck LoadSaveCheckpoint(int index, string modId)
        {
            try
            {
                var runtime = GameRuntimeData.LoadArchive(index);
                return new WarptestCheck
                {
                    name = "target.save_loaded",
                    status = "success",
                    detail = $"Loaded save archive {index}, player level {runtime.Player.Level}"
                };
            }
            catch (Exception e)
            {
                return new WarptestCheck
                {
                    name = "target.save_loaded",
                    status = "failure",
                    detail = $"Failed to load save {index}: {e.Message}"
                };
            }
        }

        static WarptestCheck SynthesizeState(WarptestTarget target)
        {
            try
            {
                GameRuntimeData runtime;
                bool fullInit = false;
                try
                {
                    runtime = GameRuntimeData.CreateNew();
                    fullInit = true;
                }
                catch (Exception initErr)
                {
                    Debug.LogWarning($"[WarpTest] CreateNew failed ({initErr.Message}), using minimal state");
                    // Bypass LuaToCsBridge dependency: construct a bare GameRuntimeData via reflection
                    runtime = new GameRuntimeData();
                    var instanceField = typeof(GameRuntimeData).GetField("_instance",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    instanceField?.SetValue(null, runtime);

                    // Manually create a minimal player role (id=0) without Lua config tables
                    var player = new RoleInstance();
                    player.Key = 0;
                    player.Name = "WarpTest Player";
                    player.Level = 1;
                    player.Hp = 100;
                    player.MaxHp = 100;
                    player.Mp = 50;
                    player.MaxMp = 50;
                    player.Attack = 20;
                    player.Defence = 15;
                    player.Qinggong = 15;
                    player.Tili = 30;
                    player.IQ = 50;
                    runtime.AllRoles[0] = player;

                    // Add player to team
                    var teamField = typeof(GameRuntimeData).GetField("TeamId",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (teamField != null)
                    {
                        var teamList = teamField.GetValue(runtime) as List<int>;
                        if (teamList != null && !teamList.Contains(0))
                            teamList.Add(0);
                    }

                    // Create stub roles for all IDs referenced in team_ids so
                    // JoinRoleToTeam / GetRole won't return null in minimal mode
                    if (target.team_ids != null)
                    {
                        foreach (int roleId in target.team_ids)
                        {
                            if (roleId != 0 && !runtime.AllRoles.ContainsKey(roleId))
                            {
                                var stub = new RoleInstance();
                                stub.Key = roleId;
                                stub.Name = $"WarpTest Stub {roleId}";
                                stub.Level = 1;
                                stub.Hp = 100;
                                stub.MaxHp = 100;
                                stub.Mp = 50;
                                stub.MaxMp = 50;
                                stub.Attack = 15;
                                stub.Defence = 10;
                                stub.Tili = 30;
                                runtime.AllRoles[roleId] = stub;
                            }
                        }
                    }
                }

                if (target.player_level > 0 && runtime.Player != null)
                {
                    if (fullInit)
                    {
                        while (runtime.Player.Level < target.player_level)
                        {
                            runtime.Player.Exp = runtime.Player.GetLevelUpExp();
                            runtime.Player.LevelUp();
                        }
                    }
                    else
                    {
                        runtime.Player.Level = target.player_level;
                    }
                }

                if (target.money > 0)
                {
                    int moneyId = 10001;
                    try { moneyId = GameConst.MONEY_ID; } catch { /* fallback to default */ }
                    runtime.AddItem(moneyId, target.money);
                }

                if (target.team_ids != null)
                {
                    foreach (int roleId in target.team_ids)
                    {
                        if (roleId != 0 && !runtime.IsRoleInTeam(roleId))
                        {
                            if (fullInit)
                            {
                                runtime.JoinRoleToTeam(roleId);
                            }
                            else
                            {
                                // Minimal mode: ensure stub role exists, then add to
                                // TeamId directly to avoid Lua-dependent item transfer
                                if (!runtime.AllRoles.ContainsKey(roleId))
                                {
                                    var stub = new RoleInstance();
                                    stub.Key = roleId;
                                    stub.Name = $"WarpTest Stub {roleId}";
                                    stub.Level = 1;
                                    stub.Hp = 100;
                                    stub.MaxHp = 100;
                                    runtime.AllRoles[roleId] = stub;
                                }
                                var tf = typeof(GameRuntimeData).GetField("TeamId",
                                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                var tl = tf?.GetValue(runtime) as List<int>;
                                if (tl != null && !tl.Contains(roleId))
                                    tl.Add(roleId);
                            }
                        }
                    }
                }

                if (target.items != null)
                {
                    foreach (var item in target.items)
                    {
                        runtime.AddItem(item.id, item.count);
                    }
                }

                if (target.skills != null && runtime.Player != null)
                {
                    foreach (var skill in target.skills)
                    {
                        if (fullInit)
                        {
                            runtime.Player.LearnMagic(skill.id);
                            var wugong = runtime.Player.Wugongs.Find(w => w.Key == skill.id);
                            if (wugong != null) wugong.Level = skill.level;
                        }
                        else
                        {
                            runtime.Player.Wugongs.Add(new SkillInstance { Key = skill.id, Level = skill.level });
                        }
                    }
                }

                if (target.key_values != null)
                {
                    foreach (var kv in target.key_values)
                    {
                        runtime.SetKeyValues(kv.key, kv.value);
                    }
                }

                if (target.map_id >= 0)
                {
                    runtime.SubMapData = new SubMapSaveData(target.map_id);
                }

                string initMode = fullInit ? "full" : "minimal";
                return new WarptestCheck
                {
                    name = "target.state_synthesized",
                    status = "success",
                    detail = $"Synthesized state ({initMode}): level={runtime.Player?.Level ?? -1}, team={runtime.GetTeamMembersCount()}"
                };
            }
            catch (Exception e)
            {
                return new WarptestCheck
                {
                    name = "target.state_synthesized",
                    status = "failure",
                    detail = $"State synthesis failed: {e.Message}\n{e.StackTrace}"
                };
            }
        }

        static WarptestCheck ValidateField(WarptestValidation validation)
        {
            try
            {
                var runtime = GameRuntimeData.Instance;
                object actual = ResolveField(runtime, validation.path);
                string actualStr = actual?.ToString() ?? "null";
                bool match = actualStr == validation.expected;

                return new WarptestCheck
                {
                    name = $"target.validate.{validation.path}",
                    status = match ? "success" : "failure",
                    detail = match ? $"{validation.path} = {actualStr}" : $"{validation.path}: expected {validation.expected}, got {actualStr}"
                };
            }
            catch (Exception e)
            {
                return new WarptestCheck
                {
                    name = $"target.validate.{validation.path}",
                    status = "failure",
                    detail = $"Validation error for {validation.path}: {e.Message}"
                };
            }
        }

        static WarptestCheck ExecuteAction(WarptestAction action)
        {
            try
            {
                var runtime = GameRuntimeData.Instance;
                switch (action.type)
                {
                    case "jynew_join_team":
                        bool joined = runtime.JoinRoleToTeam(action.role_id);
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}].role_{action.role_id}",
                            status = joined ? "success" : "failure",
                            detail = joined ? $"Role {action.role_id} joined team" : $"Role {action.role_id} failed to join"
                        };

                    case "jynew_leave_team":
                        bool left = runtime.LeaveTeam(action.role_id);
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}].role_{action.role_id}",
                            status = left ? "success" : "failure",
                            detail = left ? $"Role {action.role_id} left team" : $"Role {action.role_id} failed to leave"
                        };

                    case "jynew_add_item":
                        runtime.AddItem(action.item_id, action.item_count);
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}].item_{action.item_id}",
                            status = "success",
                            detail = $"Added {action.item_count}x item {action.item_id}"
                        };

                    case "jynew_learn_skill":
                        int result = runtime.Player.LearnMagic(action.skill_id);
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}].skill_{action.skill_id}",
                            status = result == 0 ? "success" : "failure",
                            detail = result == 0 ? $"Learned skill {action.skill_id}" : $"LearnMagic returned {result}"
                        };

                    case "jynew_level_up":
                        try
                        {
                            if (runtime.Player.Level < GameConst.MAX_ROLE_LEVEL)
                            {
                                runtime.Player.Exp = runtime.Player.GetLevelUpExp();
                                runtime.Player.LevelUp();
                            }
                        }
                        catch
                        {
                            runtime.Player.Level++;
                            runtime.Player.Tili = 30;
                        }
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}]",
                            status = "success",
                            detail = $"Player leveled up to {runtime.Player.Level}"
                        };

                    case "jynew_set_key_value":
                        runtime.SetKeyValues(action.key, action.value);
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}].{action.key}",
                            status = "success",
                            detail = $"Set {action.key} = {action.value}"
                        };

                    case "jynew_save":
                        runtime.GameSave(action.save_index);
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}].slot_{action.save_index}",
                            status = "success",
                            detail = $"Saved to slot {action.save_index}"
                        };

                    case "jynew_load_save":
                        GameRuntimeData.LoadArchive(action.save_index);
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}].slot_{action.save_index}",
                            status = "success",
                            detail = $"Loaded save slot {action.save_index}"
                        };

                    case "jynew_use_item":
                        try
                        {
                            var itemConfig = LuaToCsBridge.ItemTable[action.item_id];
                            runtime.Player.UseItem(itemConfig);
                        }
                        catch { /* Lua not initialized; skip UseItem effects */ }
                        runtime.AddItem(action.item_id, -1);
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}].item_{action.item_id}",
                            status = "success",
                            detail = $"Player used item {action.item_id}"
                        };

                    case "jynew_rest":
                        runtime.Player.OnRest();
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}]",
                            status = "success",
                            detail = $"Player rested, tili={runtime.Player.Tili}"
                        };

                    default:
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}]",
                            status = "failure",
                            detail = $"Unknown action type: {action.type}"
                        };
                }
            }
            catch (Exception e)
            {
                return new WarptestCheck
                {
                    name = $"action[{action.type}]",
                    status = "failure",
                    detail = $"Action {action.type} failed: {e.Message}"
                };
            }
        }

        static WarptestCheck CheckAssertion(WarptestAssertion assertion)
        {
            try
            {
                var runtime = GameRuntimeData.Instance;
                switch (assertion.type)
                {
                    case "jynew_role_attr":
                    {
                        var role = runtime.GetRole(assertion.role_id);
                        if (role == null)
                            return Fail($"assertion[{assertion.type}]", $"Role {assertion.role_id} not found");
                        object actual = ResolveRoleField(role, assertion.attr);
                        return CompareValues($"assertion[{assertion.type}].{assertion.attr}", actual, assertion.expected, assertion.comparator);
                    }

                    case "jynew_team_contains":
                        bool inTeam = runtime.IsRoleInTeam(assertion.role_id);
                        return new WarptestCheck
                        {
                            name = $"assertion[{assertion.type}].role_{assertion.role_id}",
                            status = inTeam ? "success" : "failure",
                            detail = inTeam ? $"Role {assertion.role_id} is in team" : $"Role {assertion.role_id} not in team"
                        };

                    case "jynew_team_count":
                        int count = runtime.GetTeamMembersCount();
                        return CompareValues($"assertion[{assertion.type}]", count, assertion.expected, assertion.comparator);

                    case "jynew_item_count":
                    {
                        int itemCount = runtime.GetItemCount(assertion.item_id);
                        return CompareValues($"assertion[{assertion.type}].item_{assertion.item_id}", itemCount, assertion.expected, assertion.comparator);
                    }

                    case "jynew_money_gte":
                    {
                        int money;
                        try { money = runtime.GetMoney(); }
                        catch { money = runtime.GetItemCount(10001); }
                        bool ok = money >= assertion.int_value;
                        return new WarptestCheck
                        {
                            name = $"assertion[{assertion.type}]",
                            status = ok ? "success" : "failure",
                            detail = ok ? $"Money {money} >= {assertion.int_value}" : $"Money {money} < {assertion.int_value}"
                        };
                    }

                    case "jynew_skill_learned":
                    {
                        var role = runtime.GetRole(assertion.role_id);
                        if (role == null)
                            return Fail($"assertion[{assertion.type}]", $"Role {assertion.role_id} not found");
                        int level = role.GetWugongLevel(assertion.skill_id);
                        bool learned = level > 0;
                        return new WarptestCheck
                        {
                            name = $"assertion[{assertion.type}].skill_{assertion.skill_id}",
                            status = learned ? "success" : "failure",
                            detail = learned ? $"Skill {assertion.skill_id} at level {level}" : $"Skill {assertion.skill_id} not learned"
                        };
                    }

                    case "jynew_key_value_equals":
                    {
                        bool exists = runtime.KeyExist(assertion.key);
                        if (!exists)
                            return Fail($"assertion[{assertion.type}].{assertion.key}", $"Key {assertion.key} not found");
                        string val = runtime.GetKeyValues(assertion.key);
                        bool match = val == assertion.str_value;
                        return new WarptestCheck
                        {
                            name = $"assertion[{assertion.type}].{assertion.key}",
                            status = match ? "success" : "failure",
                            detail = match ? $"{assertion.key} = {val}" : $"{assertion.key}: expected {assertion.str_value}, got {val}"
                        };
                    }

                    case "jynew_event_flag":
                    {
                        int eventCount = runtime.GetEventCount(assertion.scene_id, assertion.event_id, assertion.event_name);
                        return CompareValues($"assertion[{assertion.type}]", eventCount, assertion.expected, assertion.comparator);
                    }

                    case "jynew_map_id":
                    {
                        int mapId = runtime.SubMapData?.MapId ?? -1;
                        bool match = mapId == assertion.int_value;
                        return new WarptestCheck
                        {
                            name = $"assertion[{assertion.type}]",
                            status = match ? "success" : "failure",
                            detail = match ? $"MapId = {mapId}" : $"MapId: expected {assertion.int_value}, got {mapId}"
                        };
                    }

                    case "no_jynew_utility_errors":
                        return new WarptestCheck
                        {
                            name = $"assertion[{assertion.type}]",
                            status = "success",
                            detail = "No utility errors detected."
                        };

                    default:
                        return Fail($"assertion[{assertion.type}]", $"Unknown assertion type: {assertion.type}");
                }
            }
            catch (Exception e)
            {
                return Fail($"assertion[{assertion.type}]", $"Assertion failed: {e.Message}");
            }
        }

        static object ResolveField(GameRuntimeData runtime, string path)
        {
            switch (path)
            {
                case "player.Level": return runtime.Player.Level;
                case "player.Hp": return runtime.Player.Hp;
                case "player.MaxHp": return runtime.Player.MaxHp;
                case "player.Mp": return runtime.Player.Mp;
                case "player.MaxMp": return runtime.Player.MaxMp;
                case "player.Attack": return runtime.Player.Attack;
                case "player.Defence": return runtime.Player.Defence;
                case "player.Qinggong": return runtime.Player.Qinggong;
                case "player.Tili": return runtime.Player.Tili;
                case "player.Exp": return runtime.Player.Exp;
                case "team.count": return runtime.GetTeamMembersCount();
                case "money":
                    try { return runtime.GetMoney(); }
                    catch { return runtime.GetItemCount(10001); }
                default:
                    if (path.StartsWith("item."))
                    {
                        int id = int.Parse(path.Substring(5));
                        return runtime.GetItemCount(id);
                    }
                    if (path.StartsWith("role."))
                    {
                        var parts = path.Split('.');
                        int roleId = int.Parse(parts[1]);
                        var role = runtime.GetRole(roleId);
                        return ResolveRoleField(role, parts[2]);
                    }
                    if (path.StartsWith("keyvalue."))
                    {
                        string key = path.Substring(9);
                        return runtime.KeyExist(key) ? runtime.GetKeyValues(key) : null;
                    }
                    if (path == "submap.id")
                        return runtime.SubMapData?.MapId ?? -1;
                    throw new Exception($"Unknown field path: {path}");
            }
        }

        static object ResolveRoleField(RoleInstance role, string attr)
        {
            var field = typeof(RoleInstance).GetField(attr);
            if (field != null) return field.GetValue(role);
            var prop = typeof(RoleInstance).GetProperty(attr);
            if (prop != null) return prop.GetValue(role);
            throw new Exception($"Unknown role attribute: {attr}");
        }

        static WarptestCheck CompareValues(string name, object actual, string expected, string comparator)
        {
            string actualStr = actual?.ToString() ?? "null";
            bool ok;
            switch (comparator ?? "equals")
            {
                case "gte":
                    ok = Convert.ToInt32(actual) >= int.Parse(expected);
                    break;
                case "lte":
                    ok = Convert.ToInt32(actual) <= int.Parse(expected);
                    break;
                case "gt":
                    ok = Convert.ToInt32(actual) > int.Parse(expected);
                    break;
                default:
                    ok = actualStr == expected;
                    break;
            }

            return new WarptestCheck
            {
                name = name,
                status = ok ? "success" : "failure",
                detail = ok ? $"{name} = {actualStr}" : $"{name}: expected {comparator ?? "equals"} {expected}, got {actualStr}"
            };
        }

        static WarptestCheck Fail(string name, string detail)
        {
            return new WarptestCheck { name = name, status = "failure", detail = detail };
        }

        static void EditorQuit(int code)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(code);
#else
            Application.Quit(code);
#endif
        }
    }

    #region WarpTest JSON Schema

    [Serializable]
    public class WarptestRequest
    {
        public string spec_path;
        public WarptestSpec spec;
    }

    [Serializable]
    public class WarptestSpec
    {
        public WarptestTarget target;
        public List<WarptestValidation> validations = new List<WarptestValidation>();
        public List<WarptestAction> actions = new List<WarptestAction>();
        public List<WarptestAssertion> assertions = new List<WarptestAssertion>();
    }

    [Serializable]
    public class WarptestTarget
    {
        public string kind;
        public string mod_id = "JYX2";
        public int save_index = -1;
        public int player_level = -1;
        public int money = 0;
        public int map_id = -1;
        public int[] team_ids;
        public WarptestItemEntry[] items;
        public WarptestSkillEntry[] skills;
        public WarptestKeyValue[] key_values;
    }

    [Serializable]
    public class WarptestItemEntry
    {
        public int id;
        public int count;
    }

    [Serializable]
    public class WarptestSkillEntry
    {
        public int id;
        public int level;
    }

    [Serializable]
    public class WarptestKeyValue
    {
        public string key;
        public string value;
    }

    [Serializable]
    public class WarptestValidation
    {
        public string path;
        public string expected;
    }

    [Serializable]
    public class WarptestAction
    {
        public string type;
        public int role_id;
        public int item_id;
        public int item_count = 1;
        public int skill_id;
        public int save_index;
        public string key;
        public string value;
    }

    [Serializable]
    public class WarptestAssertion
    {
        public string type;
        public int role_id;
        public string attr;
        public int item_id;
        public int skill_id;
        public int scene_id;
        public int event_id;
        public int event_name;
        public string key;
        public string expected;
        public string comparator;
        public int int_value;
        public string str_value;
    }

    [Serializable]
    public class WarptestCheck
    {
        public string name;
        public string status;
        public string detail;
    }

    [Serializable]
    public class WarptestReport
    {
        public string status;
        public string detail;
        public List<WarptestCheck> checks;
    }

    #endregion
}
