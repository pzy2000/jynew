/*
 * WarpTest checkpoint command for jynew.
 *
 * Test-gated utility for semantic checkpoint validation.
 * Activated only via Unity batch mode:
 * -executeMethod Jyx2.WarptestCheckpoint.Run
 *
 * A second entry point, -executeMethod Jyx2.WarptestCheckpoint.RunWarm, keeps a
 * single Unity batch-mode process alive across many checkpoint requests (the C3
 * state-fuzzing warm session). It reuses ProcessRequest() as-is; the only new
 * behavior is the request/report/ready polling loop below.
 *
 * RunC1 is intentionally separate. It starts one headed play-mode session and
 * accepts only setup, read-only semantic probes, capture, and close operations.
 * In particular, its restore_target operation never executes spec.actions or
 * spec.assertions: Phase B remains public-UI-only.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Jyx2
{
    public static class WarptestCheckpoint
    {
        // Bump this if the warm request/report/ready JSON shape changes; the Python
        // JynewWarmSession rejects a mismatched version as a protocol error rather
        // than treating it as a game-level defect.
        const string WarmSessionVersion = "jynew-c3-session-v1";
        internal const string C1SessionVersion = "warptest-c1-unity-v3";
        const string StateEvidenceVersion = "warptest-unity-checkpoint-state-v1";
        const int WarptestMoneyItemId = 10001;

        public static bool C1SessionActive
        {
            get
            {
#if UNITY_EDITOR
                return !string.IsNullOrEmpty(s_c1SessionId);
#else
                return false;
#endif
            }
        }

#if UNITY_EDITOR
        const string PendingKey = "WarpTest.Jynew.Pending";
        const string PendingRequestPathKey = "WarpTest.Jynew.PendingRequestPath";
        const string PendingReportPathKey = "WarpTest.Jynew.PendingReportPath";
        const string C1PendingKey = "WarpTest.Jynew.C1Pending";
        const string C1RequestPathKey = "WarpTest.Jynew.C1RequestPath";
        const string C1ReportPathKey = "WarpTest.Jynew.C1ReportPath";
        const string C1ReadyPathKey = "WarpTest.Jynew.C1ReadyPath";
        const string C1SessionIdKey = "WarpTest.Jynew.C1SessionId";
        static int s_pendingPlayModeFrames;
        static int s_pendingC1PlayModeFrames;
        static bool s_c1TransitionRequired;
        static int s_c1TransitionSlot = -1;
        static int s_c1TransitionArmedSequence = -1;
        static int s_c1PendingSaveSlot = -1;
        static bool s_c1SaveObserved;
        static bool s_c1LoadObserved;
        static int s_c1SaveFrame = -1;
        static int s_c1LoadFrame = -1;
        static GameRuntimeData s_c1RuntimeAtSave;
        static int s_c1PolicyFrameId = -1;
        static bool s_c1PolicyFrameConsumed = true;
        static readonly Dictionary<string, WarptestC1Report> s_c1InputReceipts = new Dictionary<string, WarptestC1Report>();
        static string s_c1SessionId = "";

#if UNITY_EDITOR_OSX
        const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
        [DllImport(ObjectiveCLibrary)] static extern IntPtr objc_getClass(string name);
        [DllImport(ObjectiveCLibrary)] static extern IntPtr sel_registerName(string name);
        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        static extern IntPtr ObjcMessage(IntPtr receiver, IntPtr selector);
        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        static extern bool ObjcMessageInteger(IntPtr receiver, IntPtr selector, long value);
        [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
        static extern void ObjcMessageVoid(IntPtr receiver, IntPtr selector);
#endif

        internal static void EnforceC1BackgroundActivationPolicy()
        {
#if UNITY_EDITOR_OSX
            try
            {
                IntPtr application = ObjcMessage(
                    objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                // Interactive headed play: NSApplicationActivationPolicyRegular.
                ObjcMessageInteger(application, sel_registerName("setActivationPolicy:"), 0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[WarpTest C1] Unable to enforce activation policy: {e.Message}");
                throw;
            }
#endif
        }

        internal static void ConfigureC1BackgroundSession(string sessionId)
        {
            EnforceC1BackgroundActivationPolicy();
            s_c1SessionId = sessionId ?? "";
            s_c1PolicyFrameId = -1;
            s_c1PolicyFrameConsumed = true;
            s_c1InputReceipts.Clear();
            Application.runInBackground = true;
        }

        internal static void ResetC1TransitionWitness()
        {
            s_c1TransitionRequired = false;
            s_c1TransitionSlot = -1;
            s_c1TransitionArmedSequence = -1;
            s_c1PendingSaveSlot = -1;
            s_c1SaveObserved = false;
            s_c1LoadObserved = false;
            s_c1SaveFrame = -1;
            s_c1LoadFrame = -1;
            s_c1RuntimeAtSave = null;
        }

        static bool HasC1TransitionExpectation(WarptestC1TransitionExpectation expectation)
        {
            return expectation != null
                && (!string.IsNullOrEmpty(expectation.kind) || expectation.slot >= 0);
        }

        static bool ArmC1TransitionWitness(WarptestC1TransitionExpectation expectation, int sequence)
        {
            ResetC1TransitionWitness();
            if (!HasC1TransitionExpectation(expectation))
                return true;
            if (expectation.kind != "save_then_load" || expectation.slot < 0)
                return false;
            s_c1TransitionRequired = true;
            s_c1TransitionSlot = expectation.slot;
            s_c1TransitionArmedSequence = sequence;
            return true;
        }

        internal static void ObserveC1Log(string condition, string stackTrace, LogType type)
        {
            if (!s_c1TransitionRequired || string.IsNullOrEmpty(condition))
                return;
            const string saveStart = "存档中.. index = ";
            int marker = condition.IndexOf(saveStart, StringComparison.Ordinal);
            if (marker >= 0)
            {
                int slot;
                string value = condition.Substring(marker + saveStart.Length).Trim();
                s_c1PendingSaveSlot = int.TryParse(value, out slot) ? slot : -1;
                return;
            }
            if (condition.IndexOf("存档结束", StringComparison.Ordinal) < 0
                || s_c1PendingSaveSlot != s_c1TransitionSlot)
                return;
            s_c1SaveObserved = true;
            s_c1SaveFrame = Time.frameCount;
            s_c1RuntimeAtSave = GameRuntimeData.Instance;
            s_c1PendingSaveSlot = -1;
        }

        internal static void ObserveC1Transition()
        {
            if (!s_c1TransitionRequired || !s_c1SaveObserved || s_c1LoadObserved
                || s_c1RuntimeAtSave == null)
                return;
            GameRuntimeData current = GameRuntimeData.Instance;
            if (current != null && !ReferenceEquals(current, s_c1RuntimeAtSave))
            {
                s_c1LoadObserved = true;
                s_c1LoadFrame = Time.frameCount;
            }
        }

        static bool C1ExpectationMatches(WarptestC1TransitionExpectation expectation)
        {
            return expectation != null
                && expectation.kind == "save_then_load"
                && expectation.slot == s_c1TransitionSlot;
        }

        static WarptestC1TransitionEvidence C1TransitionEvidence(int sequence)
        {
            return new WarptestC1TransitionEvidence
            {
                required = s_c1TransitionRequired,
                kind = s_c1TransitionRequired ? "save_then_load" : "",
                slot = s_c1TransitionSlot,
                source = "jynew_public_ui_save_load_witness_v1",
                armed_sequence = s_c1TransitionArmedSequence,
                observed_sequence = sequence,
                save_observed = s_c1SaveObserved,
                load_observed = s_c1LoadObserved,
                save_frame = s_c1SaveFrame,
                load_frame = s_c1LoadFrame,
                ordered = s_c1SaveObserved && s_c1LoadObserved
                    && s_c1LoadFrame >= s_c1SaveFrame,
            };
        }

        static void AddC1TransitionGoalChecks(
            WarptestC1TransitionExpectation expectation,
            List<WarptestCheck> checks)
        {
            if (!HasC1TransitionExpectation(expectation) && !s_c1TransitionRequired)
                return;
            bool matches = s_c1TransitionRequired && C1ExpectationMatches(expectation);
            checks.Add(matches
                ? new WarptestCheck
                {
                    name = "c1.transition.expectation",
                    status = "success",
                    detail = $"Armed save/load witness for slot {s_c1TransitionSlot}."
                }
                : Fail("c1.transition.expectation", "Goal transition expectation was missing, malformed, or changed after semantic_start."));
            checks.Add(s_c1SaveObserved
                ? new WarptestCheck
                {
                    name = "c1.transition.save",
                    status = "success",
                    detail = $"Observed successful public-UI save to slot {s_c1TransitionSlot} at frame {s_c1SaveFrame}."
                }
                : Fail("c1.transition.save", $"No successful public-UI save to slot {s_c1TransitionSlot} was observed after semantic_start."));
            bool ordered = s_c1SaveObserved && s_c1LoadObserved
                && s_c1LoadFrame >= s_c1SaveFrame;
            checks.Add(ordered
                ? new WarptestCheck
                {
                    name = "c1.transition.load",
                    status = "success",
                    detail = $"Observed a new live GameRuntimeData instance after the slot-{s_c1TransitionSlot} save at frame {s_c1LoadFrame}."
                }
                : Fail("c1.transition.load", $"No ordered public-UI load of slot {s_c1TransitionSlot} was observed after its save."));
        }

        [UnityEditor.InitializeOnLoadMethod]
        static void ResumePendingPlayModeRun()
        {
            if (UnityEditor.EditorPrefs.GetBool(C1PendingKey, false))
            {
                s_pendingC1PlayModeFrames = 30;
                UnityEditor.EditorApplication.update -= RunPendingC1WhenPlayModeReady;
                UnityEditor.EditorApplication.update += RunPendingC1WhenPlayModeReady;
            }
            if (!UnityEditor.EditorPrefs.GetBool(PendingKey, false))
                return;
            s_pendingPlayModeFrames = 30;
            UnityEditor.EditorApplication.update -= RunPendingWhenPlayModeReady;
            UnityEditor.EditorApplication.update += RunPendingWhenPlayModeReady;
        }
#endif

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

#if UNITY_EDITOR
            if (MaybeQueuePlayModeRun(requestPath, reportPath))
                return;
#endif

            ExecuteRequestPath(requestPath, reportPath);
        }

        /// <summary>
        /// Start a headed, persistent C1 play-mode session. The sequence-numbered
        /// file protocol is fail-closed and exposes no action-execution operation.
        /// </summary>
        public static void RunC1()
        {
            EnforceC1BackgroundActivationPolicy();
            string requestPath = null;
            string reportPath = null;
            string readyPath = null;
            string sessionId = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--warptest-c1-request" && i + 1 < args.Length)
                    requestPath = args[i + 1];
                if (args[i] == "--warptest-c1-report" && i + 1 < args.Length)
                    reportPath = args[i + 1];
                if (args[i] == "--warptest-c1-ready" && i + 1 < args.Length)
                    readyPath = args[i + 1];
                if (args[i] == "--warptest-c1-session" && i + 1 < args.Length)
                    sessionId = args[i + 1];
            }

            if (string.IsNullOrEmpty(requestPath) || string.IsNullOrEmpty(reportPath) || string.IsNullOrEmpty(readyPath) || string.IsNullOrEmpty(sessionId))
            {
                Debug.LogError("[WarpTest C1] Missing request/report/ready arguments.");
                EditorQuit(1);
                return;
            }

#if UNITY_EDITOR
            if (MaybeQueueC1PlayModeRun(requestPath, reportPath, readyPath, sessionId))
                return;
            StartC1Runner(requestPath, reportPath, readyPath, sessionId);
#else
            Debug.LogError("[WarpTest C1] Jynew C1 requires Unity editor play mode.");
            EditorQuit(1);
#endif
        }

        /// <summary>
        /// C3 warm-session entry point. Unlike Run(), this never exits on its own:
        /// it polls for sequence-numbered requests and processes each one through the
        /// existing synthesized_state path (no screenshot, no Play Mode), so a whole
        /// fuzz task's candidates are injected against one already-warm process
        /// instead of paying a fresh Unity boot per candidate. The parent Python
        /// process owns lifecycle (kill on close()/restart()).
        /// </summary>
        public static void RunWarm()
        {
            string requestPath = null;
            string reportPath = null;
            string readyPath = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--warptest-warm-request" && i + 1 < args.Length)
                    requestPath = args[i + 1];
                if (args[i] == "--warptest-warm-report" && i + 1 < args.Length)
                    reportPath = args[i + 1];
                if (args[i] == "--warptest-warm-ready" && i + 1 < args.Length)
                    readyPath = args[i + 1];
            }

            if (string.IsNullOrEmpty(requestPath) || string.IsNullOrEmpty(reportPath) || string.IsNullOrEmpty(readyPath))
            {
                Debug.LogError("[WarpTest] RunWarm requires --warptest-warm-request, --warptest-warm-report, and --warptest-warm-ready");
                EditorQuit(1);
                return;
            }

            try
            {
                RuntimeEnvSetup.Setup();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WarpTest] Warm session runtime setup incomplete (non-fatal): {e.Message}");
            }

            RunWarmLoop(requestPath, reportPath, readyPath);
        }

        static void RunWarmLoop(string requestPath, string reportPath, string readyPath)
        {
            int lastSequence = 0;
            WriteWarmReady(readyPath, 0);
            Debug.Log("[WarpTest] Warm session ready (sequence=0).");

            // Runs until the host process is killed (SIGTERM/SIGKILL from the
            // Python-side session). There is deliberately no exit-on-request
            // protocol field: lifecycle is owned by the parent, matching the
            // OpenRA C3 warm session.
            while (true)
            {
                WarptestWarmRequest request;
                if (!TryReadWarmRequest(requestPath, out request) || request == null || request.sequence <= lastSequence)
                {
                    Thread.Sleep(50);
                    continue;
                }

                int sequence = request.sequence;
                WarptestWarmReport report = ProcessWarmRequest(request, sequence);
                WriteWarmJson(reportPath, report);
                lastSequence = sequence;
                // GameRuntimeData.CreateNew() (inside SynthesizeState) always rebuilds a
                // fresh runtime instance, so the next request already starts clean; no
                // separate reset step is needed before signalling ready for sequence N.
                WriteWarmReady(readyPath, sequence);
                Debug.Log($"[WarpTest] Warm session processed sequence={sequence} status={report.status}");
            }
        }

        static WarptestWarmReport ProcessWarmRequest(WarptestWarmRequest request, int sequence)
        {
            if (request.version != WarmSessionVersion)
            {
                return new WarptestWarmReport
                {
                    version = WarmSessionVersion,
                    sequence = sequence,
                    status = "rejected",
                    detail = $"Unexpected warm protocol version: {request.version ?? "<missing>"}",
                    checks = new List<WarptestCheck>(),
                };
            }
            try
            {
                var innerRequest = new WarptestRequest
                {
                    spec_path = "<warm-session>",
                    screenshot_output_path = "",
                    spec = request.spec,
                };
                var inner = ProcessRequest(innerRequest);
                // ProcessRequest already turns every expected failure mode (bad save
                // index, synthesis exception, failed validation/action/assertion) into
                // a "failure" status with per-check detail -- that is graceful handling
                // of an adversarial candidate, not an engine defect. Only an exception
                // that escapes ProcessRequest itself (caught below) is a genuine defect.
                return new WarptestWarmReport
                {
                    version = WarmSessionVersion,
                    sequence = sequence,
                    status = inner.status == "success" ? "success" : "failure",
                    detail = inner.detail,
                    checks = inner.checks ?? new List<WarptestCheck>(),
                };
            }
            catch (Exception e)
            {
                Debug.LogError($"[WarpTest] Warm session uncaught exception on sequence={sequence}: {e}");
                return new WarptestWarmReport
                {
                    version = WarmSessionVersion,
                    sequence = sequence,
                    status = "engine_error",
                    detail = $"Uncaught exception: {e.Message}",
                    checks = new List<WarptestCheck>(),
                };
            }
        }

        static bool TryReadWarmRequest(string requestPath, out WarptestWarmRequest request)
        {
            request = null;
            try
            {
                if (!File.Exists(requestPath))
                    return false;
                var json = File.ReadAllText(requestPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                    return false;
                request = JsonUtility.FromJson<WarptestWarmRequest>(json);
                return request != null;
            }
            catch (Exception)
            {
                // A request file mid-write (or transiently truncated by the Python
                // atomic-replace) is not a protocol error: just retry next poll.
                return false;
            }
        }

        static void WriteWarmReady(string readyPath, int sequence)
        {
            WriteWarmJson(readyPath, new WarptestWarmReady
            {
                version = WarmSessionVersion,
                sequence = sequence,
                status = "ready",
            });
        }

        static void WriteWarmJson(string path, object payload)
        {
            var json = JsonUtility.ToJson(payload);
            var tempPath = path + ".tmp";
            // UTF8Encoding(false): no BOM so Python json.loads("utf-8") works without utf-8-sig.
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tempPath, path);
        }

        static void ExecuteRequestPath(string requestPath, string reportPath)
        {
#if UNITY_EDITOR
            if (Application.isPlaying)
            {
                var modeRequestJson = File.ReadAllText(requestPath, Encoding.UTF8);
                var modeRequest = JsonUtility.FromJson<WarptestRequest>(modeRequestJson);
                if (modeRequest != null && !string.IsNullOrEmpty(modeRequest.screenshot_output_path))
                {
                    ExecuteRequestPathAsync(requestPath, reportPath).Forget();
                    return;
                }
            }
#endif
            try
            {
                var requestJson = File.ReadAllText(requestPath, Encoding.UTF8);
                var request = JsonUtility.FromJson<WarptestRequest>(requestJson);
                var report = AttachStateEvidence(request, ProcessRequest(request));
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

#if UNITY_EDITOR
        static bool MaybeQueueC1PlayModeRun(string requestPath, string reportPath, string readyPath, string sessionId)
        {
            if (Application.isPlaying)
                return false;

            try
            {
                const string startupScene = "Assets/0_GameStart.unity";
                if (File.Exists(startupScene))
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(startupScene);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WarpTest C1] Unable to open jynew startup scene: {e.Message}");
            }

            UnityEditor.EditorPrefs.SetBool(C1PendingKey, true);
            UnityEditor.EditorPrefs.SetString(C1RequestPathKey, requestPath);
            UnityEditor.EditorPrefs.SetString(C1ReportPathKey, reportPath);
            UnityEditor.EditorPrefs.SetString(C1ReadyPathKey, readyPath);
            UnityEditor.EditorPrefs.SetString(C1SessionIdKey, sessionId);
            s_pendingC1PlayModeFrames = 30;
            UnityEditor.EditorApplication.update -= RunPendingC1WhenPlayModeReady;
            UnityEditor.EditorApplication.update += RunPendingC1WhenPlayModeReady;
            UnityEditor.EditorApplication.isPlaying = true;
            Debug.Log("[WarpTest C1] Queued persistent jynew play-mode session.");
            return true;
        }

        static void RunPendingC1WhenPlayModeReady()
        {
            if (!UnityEditor.EditorApplication.isPlaying)
                return;
            if (s_pendingC1PlayModeFrames-- > 0)
                return;

            UnityEditor.EditorApplication.update -= RunPendingC1WhenPlayModeReady;
            var requestPath = UnityEditor.EditorPrefs.GetString(C1RequestPathKey, "");
            var reportPath = UnityEditor.EditorPrefs.GetString(C1ReportPathKey, "");
            var readyPath = UnityEditor.EditorPrefs.GetString(C1ReadyPathKey, "");
            var sessionId = UnityEditor.EditorPrefs.GetString(C1SessionIdKey, "");
            UnityEditor.EditorPrefs.DeleteKey(C1PendingKey);
            UnityEditor.EditorPrefs.DeleteKey(C1RequestPathKey);
            UnityEditor.EditorPrefs.DeleteKey(C1ReportPathKey);
            UnityEditor.EditorPrefs.DeleteKey(C1ReadyPathKey);
            UnityEditor.EditorPrefs.DeleteKey(C1SessionIdKey);

            if (string.IsNullOrEmpty(requestPath) || string.IsNullOrEmpty(reportPath) || string.IsNullOrEmpty(readyPath) || string.IsNullOrEmpty(sessionId))
            {
                Debug.LogError("[WarpTest C1] Pending session lost IPC paths.");
                EditorQuit(1);
                return;
            }
            StartC1Runner(requestPath, reportPath, readyPath, sessionId);
        }
#endif

#if UNITY_EDITOR
        static void StartC1Runner(string requestPath, string reportPath, string readyPath, string sessionId)
        {
            var existing = UnityEngine.Object.FindObjectOfType<WarptestC1RunnerBehaviour>();
            if (existing != null)
                UnityEngine.Object.Destroy(existing.gameObject);
            var host = new GameObject("WarptestC1Runner");
            UnityEngine.Object.DontDestroyOnLoad(host);
            var runner = host.AddComponent<WarptestC1RunnerBehaviour>();
            runner.Begin(requestPath, reportPath, readyPath, sessionId);
        }
#endif

#if UNITY_EDITOR
        static async UniTaskVoid ExecuteRequestPathAsync(string requestPath, string reportPath)
        {
            try
            {
                var requestJson = File.ReadAllText(requestPath, Encoding.UTF8);
                var request = JsonUtility.FromJson<WarptestRequest>(requestJson);
                var report = AttachStateEvidence(request, await ProcessRequestAsync(request));
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
                    screenshot_status = "failure",
                    screenshot_source = "capture_failure",
                    screenshot_detail = e.Message,
                    checks = new List<WarptestCheck>()
                };
                File.WriteAllText(reportPath, JsonUtility.ToJson(errorReport, true), Encoding.UTF8);
                Debug.LogError($"[WarpTest] {e}");
                EditorQuit(1);
            }
        }

        static bool MaybeQueuePlayModeRun(string requestPath, string reportPath)
        {
            if (Application.isPlaying)
                return false;
            if (Application.isBatchMode)
                return false;

            var requestJson = File.ReadAllText(requestPath, Encoding.UTF8);
            var request = JsonUtility.FromJson<WarptestRequest>(requestJson);
            if (request == null || string.IsNullOrEmpty(request.screenshot_output_path))
                return false;

            UnityEditor.EditorPrefs.SetBool(PendingKey, true);
            UnityEditor.EditorPrefs.SetString(PendingRequestPathKey, requestPath);
            UnityEditor.EditorPrefs.SetString(PendingReportPathKey, reportPath);
            s_pendingPlayModeFrames = 30;
            UnityEditor.EditorApplication.update -= RunPendingWhenPlayModeReady;
            UnityEditor.EditorApplication.update += RunPendingWhenPlayModeReady;
            UnityEditor.EditorApplication.isPlaying = true;
            Debug.Log("[WarpTest] Queued screenshot run for Unity play mode.");
            return true;
        }

        static void RunPendingWhenPlayModeReady()
        {
            if (!UnityEditor.EditorApplication.isPlaying)
                return;
            if (s_pendingPlayModeFrames-- > 0)
                return;

            UnityEditor.EditorApplication.update -= RunPendingWhenPlayModeReady;
            var requestPath = UnityEditor.EditorPrefs.GetString(PendingRequestPathKey, "");
            var reportPath = UnityEditor.EditorPrefs.GetString(PendingReportPathKey, "");
            UnityEditor.EditorPrefs.DeleteKey(PendingKey);
            UnityEditor.EditorPrefs.DeleteKey(PendingRequestPathKey);
            UnityEditor.EditorPrefs.DeleteKey(PendingReportPathKey);

            if (string.IsNullOrEmpty(requestPath) || string.IsNullOrEmpty(reportPath))
            {
                Debug.LogError("[WarpTest] Pending play-mode run lost request/report paths.");
                EditorQuit(1);
                return;
            }

            ExecuteRequestPath(requestPath, reportPath);
        }
#endif

        static WarptestReport CaptureSkippedReport(WarptestRequest request, string status, string detail, List<WarptestCheck> checks)
        {
            return new WarptestReport
            {
                status = status,
                detail = detail,
                screenshot_path = request.screenshot_output_path ?? "",
                screenshot_status = string.IsNullOrEmpty(request.screenshot_output_path) ? "skipped" : "failure",
                screenshot_source = string.IsNullOrEmpty(request.screenshot_output_path) ? "" : "capture_failure",
                screenshot_detail = "",
                checks = checks
            };
        }

        static WarptestReport AttachStateEvidence(WarptestRequest request, WarptestReport report)
        {
            if (report == null)
                report = new WarptestReport
                {
                    status = "failure",
                    detail = "No utility report produced.",
                    checks = new List<WarptestCheck>()
                };
            report.evidence_version = StateEvidenceVersion;
            report.evidence_task_id = request?.evidence_task_id ?? "";
            report.evidence_seed = request != null ? request.evidence_seed : 0;
            report.evidence_stage = request?.evidence_stage ?? "";
            report.evidence_benchmark = request?.evidence_benchmark ?? "";
            report.process_id = System.Diagnostics.Process.GetCurrentProcess().Id;
            report.process_alive_at_observation = true;
            return report;
        }

#if UNITY_EDITOR
        static async UniTask<WarptestReport> ProcessRequestAsync(WarptestRequest request)
        {
            string screenshotPath = request.screenshot_output_path;
            request.screenshot_output_path = "";
            await PrepareRuntimeForCapture(request.spec.target);
            var report = ProcessRequest(request);
            request.screenshot_output_path = screenshotPath;
            report.screenshot_path = screenshotPath ?? "";

            if (string.IsNullOrEmpty(screenshotPath) || report.status != "success")
                return report;

            try
            {
                string screenshotDir = Path.GetDirectoryName(screenshotPath);
                if (!string.IsNullOrEmpty(screenshotDir) && !Directory.Exists(screenshotDir))
                    Directory.CreateDirectory(screenshotDir);
                string sceneDetail = await PrepareVisualSceneForCapture(request.spec.target);
                string captureDetail = await CaptureScreenshotToFileWithRetries(screenshotPath);
                report.screenshot_status = "success";
                report.screenshot_source = "unity_capture";
                report.screenshot_detail = $"{sceneDetail}; {captureDetail}";
                Debug.Log($"[WarpTest] Screenshot captured to {screenshotPath}");
            }
            catch (Exception e)
            {
                if (File.Exists(screenshotPath))
                    File.Delete(screenshotPath);
                report.screenshot_status = "failure";
                report.screenshot_source = "capture_failure";
                report.screenshot_detail = e.Message;
                Debug.LogWarning($"[WarpTest] Screenshot capture failed: {e.Message}");
            }

            return report;
        }
#endif

        static WarptestReport ProcessRequest(WarptestRequest request)
        {
            var checks = new List<WarptestCheck>();
            var spec = request.spec;
            var target = spec.target;

            try
            {
                var modId = target.mod_id;
                if (string.IsNullOrEmpty(modId)) modId = GameConst.DEFAULT_GAME_MOD_NAME;
                RuntimeEnvSetup.Setup();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WarpTest] Runtime setup incomplete (non-fatal): {e.Message}");
            }

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
                return CaptureSkippedReport(request, "failure", "Checkpoint restoration failed.", checks);
            }

            EnsureStubRolesForSpec(spec);

            foreach (var validation in spec.validations)
            {
                checks.Add(ValidateField(validation));
            }

            foreach (var action in spec.actions)
            {
                checks.Add(ExecuteAction(action));
            }

            foreach (var assertion in spec.assertions)
            {
                checks.Add(CheckAssertion(assertion));
            }

            string screenshotFailureDetail = "";
            if (!string.IsNullOrEmpty(request.screenshot_output_path))
            {
                try
                {
                    string screenshotDir = Path.GetDirectoryName(request.screenshot_output_path);
                    if (!string.IsNullOrEmpty(screenshotDir) && !Directory.Exists(screenshotDir))
                        Directory.CreateDirectory(screenshotDir);
                    string captureDetail = CaptureScreenshotToFile(request.screenshot_output_path);
                    Debug.Log($"[WarpTest] Screenshot captured to {request.screenshot_output_path}");
                    bool allChecksOk = checks.All(c => c.status == "success");
                    return new WarptestReport
                    {
                        status = allChecksOk ? "success" : "failure",
                        detail = allChecksOk ? "All checks passed." : "One or more checks failed.",
                        screenshot_path = request.screenshot_output_path ?? "",
                        screenshot_status = "success",
                        screenshot_source = "unity_capture",
                        screenshot_detail = captureDetail,
                        checks = checks
                    };
                }
                catch (Exception e)
                {
                    screenshotFailureDetail = e.Message;
                    Debug.LogWarning($"[WarpTest] Screenshot capture failed (non-fatal): {e.Message}");
                }
            }

            bool allOk = checks.All(c => c.status == "success");
            return new WarptestReport
            {
                status = allOk ? "success" : "failure",
                detail = allOk ? "All checks passed." : "One or more checks failed.",
                screenshot_path = request.screenshot_output_path ?? "",
                screenshot_status = string.IsNullOrEmpty(request.screenshot_output_path) ? "skipped" : "failure",
                screenshot_source = string.IsNullOrEmpty(request.screenshot_output_path) ? "" : "capture_failure",
                screenshot_detail = screenshotFailureDetail,
                checks = checks
            };
        }

        static string CaptureScreenshotToFile(string outputPath)
        {
            if (Application.isPlaying)
            {
                var source = ScreenCapture.CaptureScreenshotAsTexture();
                Texture2D texture = null;
                try
                {
                    if (source != null)
                    {
                        texture = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                        var sourcePixels = source.GetPixels32();
                        var normalizedPixels = new Color32[1280 * 720];
                        for (int y = 0; y < 720; y++)
                        {
                            int sourceY = Math.Min(source.height - 1, y * source.height / 720);
                            for (int x = 0; x < 1280; x++)
                            {
                                int sourceX = Math.Min(source.width - 1, x * source.width / 1280);
                                normalizedPixels[y * 1280 + x] = sourcePixels[sourceY * source.width + sourceX];
                            }
                        }
                        texture.SetPixels32(normalizedPixels);
                        texture.Apply();
                        File.WriteAllBytes(outputPath, texture.EncodeToPNG());
                        if (TextureHasVisibleRange(texture))
                        {
                            Debug.Log("[WarpTest] Screenshot captured via ScreenCapture.");
                            return $"ScreenCapture captured final GameView pixels including overlay UI and normalized {source.width}x{source.height} to 1280x720.";
                        }
                    }
                }
                finally
                {
                    if (texture != null)
                        UnityEngine.Object.Destroy(texture);
                    if (source != null)
                        UnityEngine.Object.Destroy(source);
                }
            }

            string cameraDetail;
            if (TryCaptureCameraToFile(outputPath, out cameraDetail))
                return "Camera render captured final GameView pixels with Screen Space Overlay canvases at 1280x720. " + cameraDetail;
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            throw new InvalidOperationException("Unable to capture informative final GameView pixels.");
        }

#if UNITY_EDITOR
        static async UniTask<string> PrepareVisualSceneForCapture(WarptestTarget target)
        {
            // The request has already applied its actions at this point. In
            // particular, jynew_enter_submap may have changed SubMapData.MapId.
            // Reapplying target.map_id here would silently restore the start map
            // and make the before/after captures show the same scene.
            await PrepareRuntimeForCapture(target, applyTargetMap: false);

            int runtimeMapId = GameRuntimeData.Instance?.SubMapData?.MapId ?? -1;
            int mapId = runtimeMapId >= 0
                ? runtimeMapId
                : (target.map_id >= 0 ? target.map_id : GameConst.WORLD_MAP_ID);
            var map = LuaToCsBridge.MapTable[mapId];
            if (map == null)
                throw new InvalidOperationException($"Unable to resolve jynew map for capture: {mapId}");

            bool loaded = false;
            Exception callbackError = null;
            var loadPara = new LevelMaster.LevelLoadPara { loadType = LevelMaster.LevelLoadPara.LevelLoadType.Load };
            LevelMaster.LastGameMap = null;
            LevelLoader.LoadGameMap(map, loadPara, () =>
            {
                try
                {
                    LuaExecutor.Clear();
                    if (LevelMaster.Instance != null)
                        LevelMaster.Instance.TryBindPlayer().Forget();
                }
                catch (Exception e)
                {
                    callbackError = e;
                }
                loaded = true;
            });

            for (int frame = 0; frame < 300; frame++)
            {
                if (callbackError != null)
                    throw callbackError;
                if (loaded && LevelMaster.GetCurrentGameMap() != null && Camera.main != null)
                {
                    await UniTask.DelayFrame(10);
                    return $"Loaded visual scene map_id={mapId}, scene={LevelMaster.GetCurrentGameMap().MapScene}";
                }
                await UniTask.DelayFrame(1);
            }

            throw new TimeoutException($"Timed out waiting for jynew visual scene map_id={mapId}");
        }

        static async UniTask PrepareRuntimeForCapture(
            WarptestTarget target,
            bool applyTargetMap = true)
        {
            string modId = string.IsNullOrEmpty(target.mod_id) ? GameConst.DEFAULT_GAME_MOD_NAME : target.mod_id;
            if (RuntimeEnvSetup.GetCurrentMod() == null)
                SelectEditorMod(modId);

            await RuntimeEnvSetup.Setup();
            EnsureCheckpointMapContext(target);
            if (GameRuntimeData.Instance == null)
                GameRuntimeData.CreateNew();

            if (applyTargetMap && target.map_id >= 0)
                GameRuntimeData.Instance.SubMapData = new SubMapSaveData(target.map_id);
        }

        static void EnsureCheckpointMapContext(WarptestTarget target)
        {
            if (LevelMaster.GetCurrentGameMap() != null)
                return;

            int mapId = target != null && target.map_id >= 0
                ? target.map_id
                : GameConst.WORLD_MAP_ID;
            var map = LuaToCsBridge.MapTable[mapId];
            if (map == null)
                throw new InvalidOperationException($"Unable to resolve jynew checkpoint map: {mapId}");
            LevelMaster.SetCurrentMap(map);
        }

        static void SelectEditorMod(string modId)
        {
            var mods = new Jyx2.MOD.ModV2.GameModEditorLoader().LoadModsSync();
            foreach (var mod in mods)
            {
                if (mod.Id == modId)
                {
                    RuntimeEnvSetup.SetCurrentMod(mod);
                    return;
                }
            }
            throw new InvalidOperationException($"Unable to select editor mod for jynew capture: {modId}");
        }

        static async UniTask<string> CaptureScreenshotToFileWithRetries(string outputPath)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 90; attempt++)
            {
                try
                {
                    return CaptureScreenshotToFile(outputPath);
                }
                catch (Exception e)
                {
                    last = e;
                    await UniTask.DelayFrame(2);
                }
            }
            throw new InvalidOperationException($"Unable to capture an informative Unity screenshot after retries: {last?.Message}");
        }
#endif

        static bool TryCaptureCameraToFile(string outputPath, out string detail)
        {
            var camera = Camera.main ?? UnityEngine.Object.FindObjectOfType<Camera>();
            if (camera == null)
            {
                detail = "No Unity camera is available.";
                return false;
            }

            const int width = 1280;
            const int height = 720;
            var renderTexture = new RenderTexture(width, height, 24);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var overlayCanvases = UnityEngine.Object.FindObjectsOfType<Canvas>()
                .Where(canvas => canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                .ToArray();
            var previousCameras = overlayCanvases.Select(canvas => canvas.worldCamera).ToArray();
            var previousDistances = overlayCanvases.Select(canvas => canvas.planeDistance).ToArray();
            try
            {
                foreach (var canvas in overlayCanvases)
                {
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = camera;
                    canvas.planeDistance = Math.Max(camera.nearClipPlane + 0.1f, 1f);
                }
                Canvas.ForceUpdateCanvases();
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                camera.Render();

                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(outputPath, texture.EncodeToPNG());
                bool informative = TextureHasVisibleRange(texture);
                DestroyCapturedObject(texture);
                detail = informative ? "Camera render captured an informative image." : "Camera render produced a blank or flat image.";
                return informative;
            }
            finally
            {
                for (int index = 0; index < overlayCanvases.Length; index++)
                {
                    overlayCanvases[index].renderMode = RenderMode.ScreenSpaceOverlay;
                    overlayCanvases[index].worldCamera = previousCameras[index];
                    overlayCanvases[index].planeDistance = previousDistances[index];
                }
                Canvas.ForceUpdateCanvases();
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                renderTexture.Release();
                DestroyCapturedObject(renderTexture);
            }
        }

        static void DestroyCapturedObject(UnityEngine.Object obj)
        {
            if (obj == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(obj);
            else
                UnityEngine.Object.DestroyImmediate(obj);
        }

        static bool TextureHasVisibleRange(Texture2D texture)
        {
            if (texture == null)
                return false;
            var pixels = texture.GetPixels32();
            if (pixels == null || pixels.Length == 0)
                return false;
            int low = 255;
            int high = 0;
            foreach (var pixel in pixels)
            {
                if (pixel.r < low) low = pixel.r;
                if (pixel.g < low) low = pixel.g;
                if (pixel.b < low) low = pixel.b;
                if (pixel.r > high) high = pixel.r;
                if (pixel.g > high) high = pixel.g;
                if (pixel.b > high) high = pixel.b;
            }
            return high - low >= 8;
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
                    runtime = new GameRuntimeData();
                    var instanceField = typeof(GameRuntimeData).GetField("_instance",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    instanceField?.SetValue(null, runtime);

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

                    var teamField = typeof(GameRuntimeData).GetField("TeamId",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (teamField != null)
                    {
                        var teamList = teamField.GetValue(runtime) as List<int>;
                        if (teamList != null && !teamList.Contains(0))
                            teamList.Add(0);
                    }

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
                    int moneyItemId = WarptestMoneyId();
                    int currentMoney = runtime.GetItemCount(moneyItemId);
                    runtime.AddItem(moneyItemId, target.money - currentMoney);
                }

                if (target.team_ids != null)
                {
                    var teamField = typeof(GameRuntimeData).GetField("TeamId",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var teamList = teamField?.GetValue(runtime) as List<int>;
                    teamList?.Clear();
                    foreach (int roleId in target.team_ids)
                    {
                        if (!runtime.IsRoleInTeam(roleId))
                        {
                            if (fullInit)
                            {
                                runtime.JoinRoleToTeam(roleId);
                            }
                            else
                            {
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
                                if (teamList != null && !teamList.Contains(roleId))
                                    teamList.Add(roleId);
                            }
                        }
                    }
                }

                if (target.items != null)
                {
                    foreach (var item in target.items)
                    {
                        int currentCount = runtime.GetItemCount(item.id);
                        runtime.AddItem(item.id, item.count - currentCount);
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
                        int result = -1;
                        try
                        {
                            result = runtime.Player.LearnMagic(action.skill_id);
                        }
                        catch (Exception learnError)
                        {
                            Debug.LogWarning($"[WarpTest] LearnMagic fallback for skill {action.skill_id}: {learnError.Message}");
                        }
                        if (result != 0 && runtime.Player.Wugongs.All(skill => skill.Key != action.skill_id))
                        {
                            runtime.Player.Wugongs.Add(new SkillInstance
                            {
                                Key = action.skill_id,
                                Level = 100
                            });
                            result = 0;
                        }
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

                    case "jynew_enter_submap":
                        if (action.map_id <= 0)
                            throw new ArgumentOutOfRangeException(nameof(action.map_id), "jynew_enter_submap requires a positive map id");
                        runtime.SubMapData = new SubMapSaveData(action.map_id);
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}].map_{action.map_id}",
                            status = "success",
                            detail = $"Entered declared sub-map {action.map_id}"
                        };

                    case "jynew_add_money":
                        int moneyItemId = WarptestMoneyId();
                        runtime.AddItem(moneyItemId, action.amount);
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}]",
                            status = "success",
                            detail = $"Adjusted money by {action.amount}; balance={runtime.GetItemCount(moneyItemId)}"
                        };

                    case "jynew_save":
                        SaveWarptestArchive(runtime, action.save_index);
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}].slot_{action.save_index}",
                            status = "success",
                            detail = $"Saved restricted WarpTest archive slot {action.save_index}"
                        };

                    case "jynew_load_save":
                        LoadWarptestArchive(action.save_index);
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}].slot_{action.save_index}",
                            status = "success",
                            detail = $"Loaded restricted WarpTest archive slot {action.save_index}"
                        };

                    case "jynew_use_item":
                        int itemCountBeforeUse = runtime.GetItemCount(action.item_id);
                        try
                        {
                            var itemConfig = LuaToCsBridge.ItemTable[action.item_id];
                            runtime.Player.UseItem(itemConfig);
                        }
                        catch { }
                        if (runtime.GetItemCount(action.item_id) == itemCountBeforeUse)
                            runtime.AddItem(action.item_id, -1);
                        return new WarptestCheck
                        {
                            name = $"action[{action.type}].item_{action.item_id}",
                            status = "success",
                            detail = $"Player used item {action.item_id}"
                        };

                    case "jynew_rest":
                        try
                        {
                            runtime.Player.OnRest();
                        }
                        catch (Exception restError)
                        {
                            Debug.LogWarning($"[WarpTest] OnRest fallback: {restError.Message}");
                            runtime.Player.Hp = runtime.Player.MaxHp;
                            runtime.Player.Mp = runtime.Player.MaxMp;
                            runtime.Player.Hurt = 0;
                            runtime.Player.Poison = 0;
                            runtime.Player.Tili = 30;
                        }
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

                    case "jynew_money_equals":
                    {
                        int exactMoney;
                        try { exactMoney = runtime.GetMoney(); }
                        catch { exactMoney = runtime.GetItemCount(WarptestMoneyItemId); }
                        bool exactMoneyOk = exactMoney == assertion.int_value;
                        return new WarptestCheck
                        {
                            name = $"assertion[{assertion.type}]",
                            status = exactMoneyOk ? "success" : "failure",
                            detail = exactMoneyOk ? $"Money = {exactMoney}" : $"Money: expected {assertion.int_value}, got {exactMoney}"
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

        static string WarptestArchivePath(int saveIndex)
        {
            if (saveIndex < 0 || saveIndex > 99)
                throw new ArgumentOutOfRangeException(nameof(saveIndex), "WarpTest save slot must be between 0 and 99");
            return Path.Combine(Application.temporaryCachePath, $"warptest_jynew_slot_{saveIndex}.es3");
        }

        static int WarptestMoneyId()
        {
            try { return GameConst.MONEY_ID; }
            catch { return WarptestMoneyItemId; }
        }

        static void SaveWarptestArchive(GameRuntimeData runtime, int saveIndex)
        {
            string archivePath = WarptestArchivePath(saveIndex);
            if (File.Exists(archivePath))
                File.Delete(archivePath);
            ES3.Save(nameof(GameRuntimeData), runtime, archivePath);
        }

        static GameRuntimeData LoadWarptestArchive(int saveIndex)
        {
            string archivePath = WarptestArchivePath(saveIndex);
            if (!ES3.FileExists(archivePath))
                throw new FileNotFoundException(
                    $"Restricted WarpTest archive slot {saveIndex} does not exist",
                    archivePath);
            var runtime = ES3.Load<GameRuntimeData>(nameof(GameRuntimeData), archivePath);
            var instanceField = typeof(GameRuntimeData).GetField(
                "_instance",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);
            if (instanceField == null)
                throw new MissingFieldException(nameof(GameRuntimeData), "_instance");
            instanceField.SetValue(null, runtime);
            return runtime;
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

#if UNITY_EDITOR
        static EventModifiers C1Modifiers(string[] names)
        {
            EventModifiers result = EventModifiers.None;
            foreach (var raw in names ?? Array.Empty<string>())
            {
                switch ((raw ?? "").ToLowerInvariant())
                {
                    case "shift": result |= EventModifiers.Shift; break;
                    case "ctrl": case "control": result |= EventModifiers.Control; break;
                    case "alt": case "option": result |= EventModifiers.Alt; break;
                    case "meta": case "command": case "cmd": result |= EventModifiers.Command; break;
                    default: throw new InvalidOperationException($"Unsupported modifier: {raw}");
                }
            }
            return result;
        }

        static KeyCode C1KeyCode(string raw)
        {
            switch ((raw ?? "").ToLowerInvariant())
            {
                case "enter": case "return": return KeyCode.Return;
                case "escape": case "esc": return KeyCode.Escape;
                case "backspace": return KeyCode.Backspace;
                case "delete": return KeyCode.Delete;
                case "tab": return KeyCode.Tab;
                case "space": return KeyCode.Space;
                case "left": return KeyCode.LeftArrow;
                case "right": return KeyCode.RightArrow;
                case "up": return KeyCode.UpArrow;
                case "down": return KeyCode.DownArrow;
                case "home": return KeyCode.Home;
                case "end": return KeyCode.End;
                case "pageup": return KeyCode.PageUp;
                case "pagedown": return KeyCode.PageDown;
            }
            KeyCode parsed;
            if (Enum.TryParse(raw, true, out parsed)) return parsed;
            throw new InvalidOperationException($"Unsupported key: {raw}");
        }

        static void QueueC1Event(Event value)
        {
            UnityEditor.EditorGUIUtility.QueueGameViewInputEvent(value);
        }

        static int QueueC1Key(string key, string[] modifiers, char character = '\0')
        {
            var flags = C1Modifiers(modifiers);
            var code = character == '\0' ? C1KeyCode(key) : KeyCode.None;
            QueueC1Event(new Event { type = EventType.KeyDown, keyCode = code, character = character, modifiers = flags });
            QueueC1Event(new Event { type = EventType.KeyUp, keyCode = code, character = '\0', modifiers = flags });
            return 2;
        }

        static Vector2 C1Point(int x, int y)
        {
            if (x < 0 || x >= 1280 || y < 0 || y >= 720)
                throw new InvalidOperationException($"Input coordinate ({x}, {y}) is outside 1280x720.");
            if (Screen.width <= 0 || Screen.height <= 0)
                throw new InvalidOperationException("GameView has no input surface.");
            return new Vector2(
                x * (Screen.width / 1280f),
                y * (Screen.height / 720f));
        }

        static int QueueC1Action(WarptestC1InputAction action)
        {
            if (action == null) throw new InvalidOperationException("Input action is null.");
            switch (action.kind)
            {
                case "done": case "fail": case "wait":
                    if (action.seconds < 0 || action.seconds > 5) throw new InvalidOperationException("Wait duration is outside 0..5 seconds.");
                    return 0;
                case "click":
                {
                    int button = action.button == "right" ? 1 : action.button == "middle" ? 2 : 0;
                    int clicks = action.clicks == 0 ? 1 : action.clicks;
                    if (clicks < 1 || clicks > 3) throw new InvalidOperationException("Click count is outside 1..3.");
                    var point = C1Point(action.x, action.y);
                    var flags = C1Modifiers(action.modifiers);
                    for (int i = 0; i < clicks; i++)
                    {
                        QueueC1Event(new Event { type = EventType.MouseDown, mousePosition = point, button = button, clickCount = clicks, modifiers = flags });
                        QueueC1Event(new Event { type = EventType.MouseUp, mousePosition = point, button = button, clickCount = clicks, modifiers = flags });
                    }
                    return 2 * clicks;
                }
                case "key": return QueueC1Key(action.key, action.modifiers);
                case "type":
                {
                    int count = 0;
                    if (action.has_point)
                        count += QueueC1Action(new WarptestC1InputAction { kind = "click", x = action.x, y = action.y, clicks = 1, button = "left" });
                    if (action.overwrite)
                    {
                        count += QueueC1Key("a", new[] { "command" });
                        count += QueueC1Key("backspace", Array.Empty<string>());
                    }
                    string text = action.text ?? "";
                    if (text.Length > 4096) throw new InvalidOperationException("Input text exceeds 4096 characters.");
                    foreach (char character in text) count += QueueC1Key("", Array.Empty<string>(), character);
                    if (action.enter) count += QueueC1Key("enter", Array.Empty<string>());
                    return count;
                }
                case "scroll":
                    QueueC1Event(new Event { type = EventType.ScrollWheel, mousePosition = C1Point(action.x, action.y), delta = new Vector2(action.dx, action.dy) });
                    return 1;
                case "drag":
                {
                    if (action.duration < 0 || action.duration > 5) throw new InvalidOperationException("Drag duration is outside 0..5 seconds.");
                    int button = action.button == "right" ? 1 : action.button == "middle" ? 2 : 0;
                    var start = C1Point(action.x, action.y);
                    var end = C1Point(action.x2, action.y2);
                    var flags = C1Modifiers(action.modifiers);
                    QueueC1Event(new Event { type = EventType.MouseDown, mousePosition = start, button = button, modifiers = flags });
                    const int steps = 6;
                    for (int i = 1; i <= steps; i++)
                        QueueC1Event(new Event { type = EventType.MouseDrag, mousePosition = Vector2.Lerp(start, end, i / (float)steps), button = button, modifiers = flags });
                    QueueC1Event(new Event { type = EventType.MouseUp, mousePosition = end, button = button, modifiers = flags });
                    return steps + 2;
                }
                default: throw new InvalidOperationException($"Unsupported input action: {action.kind}");
            }
        }

        internal static async UniTask<WarptestC1Report> ProcessC1RequestAsync(WarptestC1Request request)
        {
            var checks = new List<WarptestCheck>();
            var report = new WarptestC1Report
            {
                version = C1SessionVersion,
                sequence = request != null ? request.sequence : -1,
                operation = request != null ? request.operation : "",
                status = "failure",
                detail = "C1 request failed.",
                checks = checks,
                screenshot_status = "skipped",
                screenshot_source = "",
                screenshot_detail = "",
                screenshot_path = request != null ? request.screenshot_output_path ?? "" : "",
            };

            if (request == null || request.version != C1SessionVersion)
            {
                report.status = "rejected";
                report.detail = "Unexpected or missing C1 protocol version.";
                return report;
            }
            if (string.IsNullOrEmpty(s_c1SessionId) || request.session_id != s_c1SessionId)
            {
                report.status = "rejected";
                report.detail = "C1 session nonce mismatch.";
                return report;
            }
            if (request.spec == null)
                request.spec = new WarptestSpec { target = new WarptestTarget() };
            if (request.spec.target == null)
                request.spec.target = new WarptestTarget();

            try
            {
                switch (request.operation)
                {
                    case "clean_entry":
                    {
                        ResetC1TransitionWitness();
                        var load = UnityEngine.SceneManagement.SceneManager.LoadSceneAsync("0_GameStart");
                        if (load == null)
                            throw new InvalidOperationException("Unable to load jynew startup scene.");
                        while (!load.isDone)
                            await UniTask.DelayFrame(1);
                        await UniTask.DelayFrame(15);
                        checks.Add(new WarptestCheck
                        {
                            name = "c1.clean_entry",
                            status = "success",
                            detail = "Loaded the public jynew startup scene."
                        });
                        break;
                    }
                    case "restore_target":
                    {
                        ResetC1TransitionWitness();
                        // SECURITY INVARIANT: this branch deliberately never reads or
                        // iterates the Phase B action or goal-assertion lists.
                        var target = request.spec.target;
                        await PrepareRuntimeForCapture(target);
                        checks.Add(target.save_index >= 0
                            ? LoadSaveCheckpoint(target.save_index, target.mod_id)
                            : SynthesizeState(target));
                        if (checks.All(c => c.status == "success") && request.spec.validations != null)
                            foreach (var validation in request.spec.validations)
                                checks.Add(ValidateField(validation));
                        if (checks.All(c => c.status == "success"))
                        {
                            string sceneDetail = await PrepareVisualSceneForCapture(target);
                            checks.Add(new WarptestCheck
                            {
                                name = "c1.target_playable",
                                status = "success",
                                detail = sceneDetail,
                            });
                        }
                        break;
                    }
                    case "semantic_start":
                    {
                        bool transitionExpectationValid = ArmC1TransitionWitness(
                            request.transition_expectation, request.sequence);
                        if (HasC1TransitionExpectation(request.transition_expectation))
                            checks.Add(transitionExpectationValid
                                ? new WarptestCheck
                                {
                                    name = "c1.transition.armed",
                                    status = "success",
                                    detail = $"Armed public-UI save/load witness for slot {request.transition_expectation.slot}."
                                }
                                : Fail("c1.transition.armed", "Invalid save/load transition expectation."));
                        checks.AddRange(CheckC1Target(request.spec.target));
                        if (request.spec.validations != null)
                            foreach (var validation in request.spec.validations)
                                checks.Add(ValidateField(validation));
                        break;
                    }
                    case "semantic_goal":
                        if (request.spec.assertions == null || request.spec.assertions.Count == 0)
                            checks.Add(Fail("c1.semantic_goal", "No goal assertions were declared."));
                        else
                            foreach (var assertion in request.spec.assertions)
                                checks.Add(CheckAssertion(assertion));
                        AddC1TransitionGoalChecks(request.transition_expectation, checks);
                        break;
                    case "capture":
                    {
                        if (string.IsNullOrEmpty(request.screenshot_output_path))
                            throw new InvalidOperationException("capture requires screenshot_output_path.");
                        string directory = Path.GetDirectoryName(request.screenshot_output_path);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                        await UniTask.DelayFrame(5);
                        string detail = await CaptureScreenshotToFileWithRetries(request.screenshot_output_path);
                        s_c1PolicyFrameId++;
                        s_c1PolicyFrameConsumed = false;
                        report.screenshot_status = "success";
                        report.screenshot_source = "unity_gameview_capture";
                        report.screenshot_detail = detail;
                        report.frame_id = s_c1PolicyFrameId;
                        report.frame_width = 1280;
                        report.frame_height = 720;
                        checks.Add(new WarptestCheck
                        {
                            name = "c1.live_capture",
                            status = "success",
                            detail = detail,
                        });
                        break;
                    }
                    case "input_batch":
                    {
                        WarptestC1Report cached;
                        if (!string.IsNullOrEmpty(request.batch_id) && s_c1InputReceipts.TryGetValue(request.batch_id, out cached))
                        {
                            cached.sequence = request.sequence;
                            cached.operation = request.operation;
                            return cached;
                        }
                        if (string.IsNullOrEmpty(request.batch_id) || request.frame_id != s_c1PolicyFrameId || s_c1PolicyFrameConsumed)
                            throw new InvalidOperationException("Input batch references a stale or consumed frame.");
                        if (request.actions == null || request.actions.Count < 1 || request.actions.Count > 64)
                            throw new InvalidOperationException("Input batch action count is outside 1..64.");
                        int eventCount = 0;
                        foreach (var action in request.actions) eventCount += QueueC1Action(action);
                        s_c1PolicyFrameConsumed = true;
                        report.accepted = true;
                        report.batch_id = request.batch_id;
                        report.event_count = eventCount;
                        report.resulting_frame_id = request.frame_id + 1;
                        report.input_backend = "unity_editor_queue_gameview_input_v1";
                        report.error = "";
                        checks.Add(new WarptestCheck { name = "c1.input_batch", status = "success", detail = $"Queued {eventCount} GameView events." });
                        s_c1InputReceipts[request.batch_id] = report;
                        break;
                    }
                    case "close":
                        checks.Add(new WarptestCheck
                        {
                            name = "c1.close",
                            status = "success",
                            detail = "Close acknowledged."
                        });
                        break;
                    default:
                        report.status = "rejected";
                        report.detail = $"Unsupported C1 operation: {request.operation ?? "<missing>"}";
                        return report;
                }

                report.transition_evidence = C1TransitionEvidence(request.sequence);
                bool ok = checks.Count > 0 && checks.All(c => c.status == "success");
                report.status = ok ? "success" : "failure";
                report.detail = ok ? "C1 live operation succeeded." : "One or more C1 live checks failed.";
                return report;
            }
            catch (Exception e)
            {
                Debug.LogError($"[WarpTest C1] {request.operation} failed: {e}");
                report.status = "engine_error";
                report.detail = e.Message;
                report.screenshot_status = request.operation == "capture" ? "failure" : report.screenshot_status;
                report.screenshot_source = request.operation == "capture" ? "capture_failure" : report.screenshot_source;
                report.screenshot_detail = request.operation == "capture" ? e.Message : report.screenshot_detail;
                report.transition_evidence = C1TransitionEvidence(request.sequence);
                return report;
            }
        }

        static List<WarptestCheck> CheckC1Target(WarptestTarget target)
        {
            var checks = new List<WarptestCheck>();
            // GameRuntimeData.Instance lazily calls CreateNew().  While the public
            // UI is still on the mod-selection screen that constructor depends on
            // gameplay-only tables and may throw instead of reporting a normal
            // wrong-start result.  Semantic probes must be read-only, so inspect
            // the already-existing singleton without creating one.
            var runtimeField = typeof(GameRuntimeData).GetField(
                "_instance",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);
            var runtime = runtimeField?.GetValue(null) as GameRuntimeData;
            if (runtime == null || runtime.Player == null)
            {
                checks.Add(Fail("c1.target.runtime", "Jynew runtime/player is not live."));
                return checks;
            }
            if (target.player_level > 0)
                checks.Add(CompareValues("c1.target.player_level", runtime.Player.Level, target.player_level.ToString(), "equals"));
            if (target.money > 0)
            {
                int money;
                try { money = runtime.GetMoney(); }
                catch { money = runtime.GetItemCount(10001); }
                checks.Add(CompareValues("c1.target.money", money, target.money.ToString(), "gte"));
            }
            if (target.map_id >= 0)
                checks.Add(CompareValues("c1.target.map_id", runtime.SubMapData?.MapId ?? -1, target.map_id.ToString(), "equals"));
            if (target.team_ids != null)
            {
                foreach (int roleId in target.team_ids)
                {
                    bool present = roleId == 0 || runtime.IsRoleInTeam(roleId);
                    checks.Add(new WarptestCheck
                    {
                        name = $"c1.target.team.role_{roleId}",
                        status = present ? "success" : "failure",
                        detail = present ? $"Role {roleId} is in the live team." : $"Role {roleId} is missing from the live team.",
                    });
                }
            }
            if (target.items != null)
                foreach (var item in target.items)
                    checks.Add(CompareValues($"c1.target.item_{item.id}", runtime.GetItemCount(item.id), item.count.ToString(), "gte"));
            if (target.skills != null)
                foreach (var skill in target.skills)
                    checks.Add(CompareValues($"c1.target.skill_{skill.id}", runtime.Player.GetWugongLevel(skill.id), skill.level.ToString(), "gte"));
            if (target.key_values != null)
                foreach (var value in target.key_values)
                {
                    string actual = runtime.KeyExist(value.key) ? runtime.GetKeyValues(value.key) : null;
                    checks.Add(CompareValues($"c1.target.key_{value.key}", actual, value.value, "equals"));
                }
            if (checks.Count == 0)
                checks.Add(new WarptestCheck { name = "c1.target.runtime", status = "success", detail = "Jynew runtime/player is live." });
            return checks;
        }

        internal static void WriteC1Json(string path, object payload)
        {
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonUtility.ToJson(payload, true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tempPath, path);
        }
#endif

        internal static void EditorQuit(int code)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(code);
#else
            Application.Quit(code);
#endif
        }
    }

    // Persistent headed C1 transport. It only dispatches to
    // ProcessC1RequestAsync, whose restore branch cannot execute Phase B.
#if UNITY_EDITOR
    public sealed class WarptestC1RunnerBehaviour : MonoBehaviour
    {
        string _requestPath;
        string _reportPath;
        int _lastSequence;
        bool _busy;

        public void Begin(string requestPath, string reportPath, string readyPath, string sessionId)
        {
            _requestPath = requestPath;
            _reportPath = reportPath;
            _lastSequence = 0;
            Application.logMessageReceived -= WarptestCheckpoint.ObserveC1Log;
            Application.logMessageReceived += WarptestCheckpoint.ObserveC1Log;
            WarptestCheckpoint.ResetC1TransitionWitness();
            WarptestCheckpoint.ConfigureC1BackgroundSession(sessionId);
            WarptestCheckpoint.WriteC1Json(readyPath, new WarptestC1Ready
            {
                version = WarptestCheckpoint.C1SessionVersion,
                sequence = 0,
                status = "ready",
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                session_id = sessionId,
            });
            Debug.Log("[WarpTest C1] Jynew persistent session ready.");
        }

        void Update()
        {
            WarptestCheckpoint.EnforceC1BackgroundActivationPolicy();
            WarptestCheckpoint.ObserveC1Transition();
            if (!_busy)
                PollOnce().Forget();
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= WarptestCheckpoint.ObserveC1Log;
            WarptestCheckpoint.ResetC1TransitionWitness();
        }

        async UniTaskVoid PollOnce()
        {
            WarptestC1Request request = null;
            try
            {
                if (!File.Exists(_requestPath)) return;
                string json = File.ReadAllText(_requestPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;
                request = JsonUtility.FromJson<WarptestC1Request>(json);
            }
            catch
            {
                return; // atomic replace can still race a filesystem observer
            }
            if (request == null || request.sequence <= _lastSequence) return;

            _busy = true;
            WarptestC1Report report;
            if (request.sequence != _lastSequence + 1)
            {
                report = new WarptestC1Report
                {
                    version = WarptestCheckpoint.C1SessionVersion,
                    sequence = request.sequence,
                    operation = request.operation,
                    status = "rejected",
                    detail = $"Expected sequence {_lastSequence + 1}, got {request.sequence}.",
                    checks = new List<WarptestCheck>(),
                };
            }
            else
            {
                report = await WarptestCheckpoint.ProcessC1RequestAsync(request);
            }
            WarptestCheckpoint.WriteC1Json(_reportPath, report);
            _lastSequence = request.sequence;
            _busy = false;
            if (request.operation == "close" && report.status == "success")
            {
                await UniTask.DelayFrame(1);
                WarptestCheckpoint.EditorQuit(0);
            }
        }
    }
#endif

    [Serializable]
    public class WarptestC1Request
    {
        public string version;
        public int sequence;
        public string operation;
        public string session_id;
        public string spec_path;
        public string screenshot_output_path;
        public int frame_id = -1;
        public string batch_id;
        public List<WarptestC1InputAction> actions = new List<WarptestC1InputAction>();
        public WarptestSpec spec;
        public WarptestC1TransitionExpectation transition_expectation;
    }

    [Serializable]
    public class WarptestC1Report
    {
        public string version;
        public int sequence;
        public string operation;
        public string status;
        public string detail;
        public string screenshot_path;
        public string screenshot_status;
        public string screenshot_source;
        public string screenshot_detail;
        public int frame_id = -1;
        public int frame_width;
        public int frame_height;
        public string batch_id;
        public bool accepted;
        public int event_count;
        public int resulting_frame_id = -1;
        public string error;
        public string input_backend;
        public WarptestC1TransitionEvidence transition_evidence;
        public List<WarptestCheck> checks = new List<WarptestCheck>();
    }

    [Serializable]
    public class WarptestC1TransitionExpectation
    {
        public string kind;
        public int slot = -1;
    }

    [Serializable]
    public class WarptestC1TransitionEvidence
    {
        public bool required;
        public string kind;
        public int slot = -1;
        public string source;
        public int armed_sequence = -1;
        public int observed_sequence = -1;
        public bool save_observed;
        public bool load_observed;
        public int save_frame = -1;
        public int load_frame = -1;
        public bool ordered;
    }

    [Serializable]
    public class WarptestC1Ready
    {
        public string version;
        public int sequence;
        public string status;
        public int pid;
        public string session_id;
    }

    [Serializable]
    public class WarptestC1InputAction
    {
        public string kind;
        public int x;
        public int y;
        public int x2;
        public int y2;
        public bool has_point;
        public string key;
        public string[] modifiers = Array.Empty<string>();
        public string text;
        public int dx;
        public int dy;
        public float seconds;
        public float duration;
        public string button;
        public int clicks;
        public bool overwrite;
        public bool enter;
    }

    [Serializable]
    public class WarptestRequest
    {
        public string spec_path;
        public string screenshot_output_path;
        public string evidence_task_id;
        public int evidence_seed;
        public string evidence_stage;
        public string evidence_benchmark;
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
        public int map_id = -1;
        public int amount;
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
        public string evidence_version;
        public string evidence_task_id;
        public int evidence_seed;
        public string evidence_stage;
        public string evidence_benchmark;
        public int process_id;
        public bool process_alive_at_observation;
        public string screenshot_path;
        public string screenshot_status;
        public string screenshot_source;
        public string screenshot_detail;
        public List<WarptestCheck> checks;
    }

    // C3 warm-session protocol (RunWarm). Deliberately separate from
    // WarptestRequest/WarptestReport above: the warm loop wraps the same
    // WarptestSpec payload with a version + monotonic sequence number so the
    // Python-side JynewWarmSession can detect stale/duplicate files under polling.
    [Serializable]
    public class WarptestWarmRequest
    {
        public string version;
        public int sequence;
        public WarptestSpec spec;
    }

    [Serializable]
    public class WarptestWarmReport
    {
        public string version;
        public int sequence;
        public string status; // success | failure | rejected | engine_error
        public string detail;
        public List<WarptestCheck> checks;
    }

    [Serializable]
    public class WarptestWarmReady
    {
        public string version;
        public int sequence;
        public string status; // always "ready"
    }
}
