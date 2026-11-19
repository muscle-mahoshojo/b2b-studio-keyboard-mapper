using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;

namespace B2BKeys
{
    internal sealed class StudioLearning
    {
        private const int CaptureTimeoutMs = 600;
        private const int LearnTimeoutMs = 8000;
        private const int PollIntervalMs = 250;
        private const string ArmedPrefix = "ARMED ";
        private const string BoundPrefix = "BOUND ";
        private const string StatusSeparator = " \u2014 ";
        private const string SourceSeparator = " \u2190 ";
        private const string SelectSeparator = ", SELECT" + SourceSeparator;
        private const string PushSuffix = "/push";

        private readonly string settingsPath;
        private readonly string source;
        private readonly string statusSource;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        private Snapshot before;
        private string armedTarget;

        private sealed class Snapshot
        {
            internal string Preset;
            internal Dictionary<string, string> Sources;
        }

        internal StudioLearning(Profile profile, Binding binding)
        {
            string userData = Environment.GetEnvironmentVariable("B2B_USERDATA");
            if (String.IsNullOrWhiteSpace(userData))
                userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "B2B Studio");
            settingsPath = Path.Combine(userData, "booth-settings.json");
            string port = profile.StudioPort ?? "";
            source = binding.MidiSource(port, profile.Channel);
            statusSource = port + " " + source.Substring(port.Length + 1);
        }

        internal async Task CaptureAsync(CancellationToken cancellation)
        {
            var observation = Task.Run(() => ReadStatus(ArmedPrefix), cancellation);
            if (await Task.WhenAny(observation, Task.Delay(CaptureTimeoutMs, cancellation)).ConfigureAwait(false) == observation)
            {
                string status = await observation.ConfigureAwait(false);
                int separator = status == null ? -1 : status.IndexOf(StatusSeparator, StringComparison.Ordinal);
                if (separator > ArmedPrefix.Length)
                {
                    string target = status.Substring(ArmedPrefix.Length, separator - ArmedPrefix.Length);
                    if (Profile.IsTargetId(target)) armedTarget = target;
                }
            }
            cancellation.ThrowIfCancellationRequested();
            before = TryReadSnapshot();
        }

        internal async Task<string> WaitForTargetAsync(CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            Task<string> observation = null;
            while (clock.ElapsedMilliseconds < LearnTimeoutMs)
            {
                cancellation.ThrowIfCancellationRequested();
                var current = TryReadSnapshot();
                if (current != null && before != null && current.Preset != before.Preset)
                    throw new InvalidOperationException("Studio 在学习期间切换了 MIDI 预设，请重新学习。");

                if (armedTarget != null)
                {
                    if (observation != null && observation.IsCompleted)
                    {
                        string target = BoundTarget(await observation.ConfigureAwait(false));
                        if (target != null) return target;
                        observation = null;
                    }
                    if (observation == null) observation = Task.Run(() => ReadStatus(BoundPrefix), cancellation);
                }

                if (before != null && current != null)
                {
                    var matches = current.Sources
                        .Where(pair => pair.Value == source && IsArmedTarget(pair.Key)
                            && (!before.Sources.ContainsKey(pair.Key) || before.Sources[pair.Key] != source))
                        .Select(pair => pair.Key).Take(2).ToArray();
                    if (matches.Length == 1) return matches[0];
                    if (matches.Length > 1 && armedTarget == null)
                        throw new InvalidOperationException("Studio 记录中有多个控件匹配这次学习信号，请逐个重新学习。");
                }

                int remaining = LearnTimeoutMs - (int)clock.ElapsedMilliseconds;
                if (remaining > 0)
                    await Task.Delay(Math.Min(PollIntervalMs, remaining), cancellation).ConfigureAwait(false);
            }

            throw new InvalidOperationException(before == null
                ? "已发送学习信号，但无法读取 Studio 当前 MIDI 预设或确认控件。请确认 Studio 已开启并进入学习模式。"
                : "已发送学习信号，但未确认控件 ID。请确认 Studio 已选中控件，且 MIDI 输入端口与通道正确。");
        }

        private bool IsArmedTarget(string target)
        {
            return armedTarget == null || target == armedTarget || target == armedTarget + PushSuffix;
        }

        private string BoundTarget(string status)
        {
            if (status == null) return null;
            string pairedPrefix = BoundPrefix + armedTarget + StatusSeparator + "turn" + SourceSeparator;
            if (status.StartsWith(pairedPrefix, StringComparison.Ordinal))
            {
                int separator = status.IndexOf(SelectSeparator, pairedPrefix.Length, StringComparison.Ordinal);
                if (separator < 0) return null;
                if (status.Substring(pairedPrefix.Length, separator - pairedPrefix.Length) == statusSource)
                    return armedTarget;
                string pushTarget = armedTarget + PushSuffix;
                return status.Substring(separator + SelectSeparator.Length) == statusSource && Profile.IsTargetId(pushTarget)
                    ? pushTarget : null;
            }

            int sourceStart = status.IndexOf(SourceSeparator, BoundPrefix.Length, StringComparison.Ordinal);
            if (sourceStart < 0) return null;
            string target = status.Substring(BoundPrefix.Length, sourceStart - BoundPrefix.Length);
            string reportedSource = status.Substring(sourceStart + SourceSeparator.Length);
            return IsArmedTarget(target) && Profile.IsTargetId(target)
                && (reportedSource == statusSource || reportedSource.StartsWith(statusSource + " ", StringComparison.Ordinal))
                ? target : null;
        }

        private Snapshot TryReadSnapshot()
        {
            try
            {
                using (var stream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    if (stream.Length > json.MaxJsonLength) return null;
                    var settings = json.DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
                    object value;
                    if (settings == null || !settings.TryGetValue("b2b.midi.profiles", out value) || !(value is string))
                        return null;
                    var profiles = json.DeserializeObject((string)value) as Dictionary<string, object>;
                    if (profiles == null || !profiles.TryGetValue("active", out value) || !(value is string))
                        return null;
                    var preset = (string)value;
                    if (!profiles.TryGetValue("data", out value)) return null;
                    var data = value as Dictionary<string, object>;
                    if (data == null || !data.TryGetValue(preset, out value)) return null;
                    var bindings = value as Dictionary<string, object>;
                    if (bindings == null) return null;

                    var sources = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var pair in bindings)
                    {
                        var binding = pair.Value as Dictionary<string, object>;
                        if (binding != null && binding.TryGetValue("src", out value) && value is string && Profile.IsTargetId(pair.Key))
                            sources.Add(pair.Key, (string)value);
                    }
                    return new Snapshot { Preset = preset, Sources = sources };
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static string ReadStatus(string prefix)
        {
            var processes = Process.GetProcessesByName("B2B Studio");
            try
            {
                foreach (var process in processes)
                {
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;
                    var window = AutomationElement.FromHandle(handle);
                    var status = window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "status"));
                    if (status != null)
                    {
                        string name = status.Current.Name;
                        if (name.StartsWith(prefix, StringComparison.Ordinal)) return name;
                    }
                    var text = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
                    foreach (AutomationElement element in (status ?? window).FindAll(TreeScope.Descendants, text))
                    {
                        string name = element.Current.Name;
                        if (name.StartsWith(prefix, StringComparison.Ordinal)) return name;
                    }
                }
            }
            catch (COMException) { }
            catch (InvalidOperationException) { }
            catch (ElementNotAvailableException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
            return null;
        }
    }
}
