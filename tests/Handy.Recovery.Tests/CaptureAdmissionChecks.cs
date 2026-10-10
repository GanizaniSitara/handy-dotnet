using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Handy;
using Handy.Services;

static class CaptureAdmissionChecks
{
    public static async Task Run()
    {
        // Do not construct/start a WPF Application. These admission branches
        // must return before touching any UI, audio, ASR, history or input.
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        var runner = new RecoveryRetypeRunner();
        Set("_retypeRunner", runner);
        var lines = new List<string>();
        var sink = typeof(App).Assembly.GetType("Handy.Log")!.GetField("Sink", BindingFlags.Static | BindingFlags.Public)!;
        var previous = sink.GetValue(null);
        sink.SetValue(null, new Action<string>(lines.Add));
        try
        {
            Set("_transcribing", true);
            Start();
            Expect("Cannot start recording while dictation or recovery is active.");
            Set("_transcribing", false);
            Set("_recording", true);
            Start();
            Expect("Cannot start recording while dictation or recovery is active.");
            typeof(App).GetMethod("RetypeLastTranscript", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
            Expect("retype-last-transcription: ignored while recording or transcription is in flight");
            Set("_recording", false);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var work = runner.RunAsync(() => false, () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("test release timeout");
            });
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new Exception("worker entry timeout");
                Start();
                Expect("Cannot start recording while dictation or recovery is active.");
            }
            finally { release.Set(); }
            await work;

            // Regression: StopAndTranscribe throwing before its internal try/finally
            // used to leak the _transcribing flag, preventing future dictations.
            Set("_transcribing", false);
            var stopMethod = typeof(App).GetMethod("StopAndTranscribe", BindingFlags.Instance | BindingFlags.NonPublic)!;
            stopMethod.Invoke(app, null); // Will throw NRE on _audio!.StopAsync since _audio is null
            // Wait for the async void method to hit the catch block and update the flag
            for (int i = 0; i < 50; i++)
            {
                if (!(bool)typeof(App).GetField("_transcribing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!) break;
                await Task.Delay(10);
            }
            if ((bool)typeof(App).GetField("_transcribing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!)
                throw new Exception("StopAndTranscribe leaked _transcribing flag on exception.");
            Expect("StopAsync threw: System.NullReferenceException");
        }
        finally { sink.SetValue(null, previous); }
        Console.WriteLine("Capture/recovery admission checks passed without constructing a WPF Application.");

        void Set(string name, object value) => typeof(App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, value);
        void Start()
        {
            var method = typeof(App).GetMethod("StartRecording", BindingFlags.Instance | BindingFlags.NonPublic)!;
            method.Invoke(app, new[] { Enum.ToObject(method.GetParameters()[0].ParameterType, 0) });
        }
        void Expect(string message)
        {
            if (!lines.Exists(x => x.Contains(message)))
            {
                Console.WriteLine("Actual lines:");
                foreach(var l in lines) Console.WriteLine(l);
                throw new Exception("Missing admission rejection: " + message);
            }
            lines.Clear();
        }
    }
}
