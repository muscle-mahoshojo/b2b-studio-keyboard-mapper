using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace B2BKeys
{
    public sealed class Binding
    {
        public string Name { get; set; }
        public string Mode { get; set; }
        public int Down { get; set; }
        public int Up { get; set; }
        public int Number { get; set; }
        public int Step { get; set; }
        public int Initial { get; set; }
        public string Targets { get; set; }

        internal bool IsNote { get { return Mode == "note"; } }
        internal bool IsRelative { get { return Mode == "relative"; } }

        internal string MidiSource(string port, int channel)
        {
            return String.Format("{0}|{1}|{2}|{3}", port, IsNote ? "note" : "cc", channel - 1, Number);
        }

        internal IEnumerable<string> TargetIds()
        {
            return (Targets ?? "").Split(',').Select(target => target.Trim()).Where(target => target.Length > 0);
        }
    }

    public sealed class Profile
    {
        internal const string DefaultPort = "B2B Keys";
        internal static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "B2B Keyboard Mapper", "keys.json");
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };

        public int Version { get; set; }
        public string Output { get; set; }
        public string Input { get; set; }
        public int Channel { get; set; }
        public List<Binding> Bindings { get; set; }

        internal string StudioPort { get { return String.IsNullOrWhiteSpace(Input) ? Output : Input; } }

        internal static Profile Defaults()
        {
            return new Profile
            {
                Version = 1, Output = DefaultPort, Input = "", Channel = 16,
                Bindings = new List<Binding>
                {
                    Make("Deck 1 · PLAY", "note", Keys.Q, Keys.None, 36, "d0@cdj-3000x:play"),
                    Make("Deck 1 · CUE", "note", Keys.W, Keys.None, 37, "d0@cdj-3000x:cue"),
                    Make("Deck 2 · PLAY", "note", Keys.O, Keys.None, 38, "d1@cdj-3000x:play"),
                    Make("Deck 2 · CUE", "note", Keys.P, Keys.None, 39, "d1@cdj-3000x:cue"),
                    Make("混音器 · CH 2 低频", "relative", Keys.A, Keys.S, 70, MixerTargets("ch1.eqLo")),
                    Make("混音器 · CH 3 低频", "relative", Keys.K, Keys.L, 71, MixerTargets("ch2.eqLo")),
                    Make("混音器 · CH 2 音量", "relative", Keys.Z, Keys.X, 72, MixerTargets("ch1.fader")),
                    Make("混音器 · CH 3 音量", "relative", Keys.N, Keys.M, 73, MixerTargets("ch2.fader")),
                    Make("混音器 · 交叉推子", "relative", Keys.Oemcomma, Keys.OemPeriod, 74, "mx@djm-a9:xf.pos")
                }
            };
        }

        private static string MixerTargets(string control)
        {
            return "mx@djm-a9:" + control + ", mx@djm-v10-lf:" + control;
        }

        private static Binding Make(string name, string mode, Keys down, Keys up, int number, string targets)
        {
            return new Binding
            {
                Name = name, Mode = mode, Down = (int)down, Up = (int)up,
                Number = number, Step = 2, Initial = 64, Targets = targets
            };
        }

        internal static Profile Load(string path)
        {
            if (new FileInfo(path).Length > Json.MaxJsonLength)
                throw new InvalidDataException("配置文件过大。");
            var profile = Json.Deserialize<Profile>(File.ReadAllText(path, Encoding.UTF8));
            if (profile == null) throw new InvalidDataException("配置文件为空。");
            profile.Validate();
            return profile;
        }

        internal void Save(string path)
        {
            Validate();
            Write(path, Json.Serialize(this));
        }

        internal void ExportPreset(string path, string port)
        {
            Validate();
            if (String.IsNullOrWhiteSpace(port) || port.Contains("|"))
                throw new InvalidDataException("请输入 Studio 里看到的 MIDI 输入端口名称。");
            var bindings = new Dictionary<string, object>();
            foreach (var binding in Bindings)
            {
                var src = binding.MidiSource(port, Channel);
                foreach (var target in binding.TargetIds())
                {
                    var mapping = new Dictionary<string, string> { { "src", src } };
                    if (binding.IsRelative) mapping.Add("rel", "2c");
                    bindings.Add(target, mapping);
                }
            }
            if (bindings.Count == 0) throw new InvalidDataException("至少填写一个 Studio 控件 ID 才能导出预设。");
            Write(path, Json.Serialize(new
            {
                app = "B2B Studio", kind = "midi-preset", version = 1, name = "B2B Keys", bindings = bindings
            }));
        }

        private static void Write(string path, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, contents + Environment.NewLine, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        internal void Validate()
        {
            if (Version != 1 || Channel < 1 || Channel > 16 || Bindings == null || Bindings.Count > 184)
                throw new InvalidDataException("无法读取此版本的配置或 MIDI 通道超出范围。");
            var keys = new HashSet<int>();
            var signals = new HashSet<string>();
            var targets = new HashSet<string>();
            foreach (var binding in Bindings)
            {
                if (binding == null || String.IsNullOrWhiteSpace(binding.Name))
                    throw new InvalidDataException("请为每条映射填写名称。");
                var name = binding.Name + "：";
                if (!binding.IsNote && !binding.IsRelative && binding.Mode != "absolute")
                    throw new InvalidDataException(name + "映射类型无效。");
                if (binding.Down == 0 && (binding.Up == 0 || binding.IsNote))
                    throw new InvalidDataException(name + "至少设置一个键。");
                if (binding.IsNote && binding.Up != 0)
                    throw new InvalidDataException(name + "按钮只使用「按下 / 减少」一栏。");
                foreach (var chord in new[] { binding.Down, binding.Up }.Where(chord => chord != 0))
                {
                    if (!Chord.CanBind(chord) || !keys.Add(chord))
                        throw new InvalidDataException(name + "键位无效或重复（" + Chord.Display(chord) + "）。");
                }
                if (binding.IsNote ? binding.Number < 0 || binding.Number > 127 : binding.Number < 64 || binding.Number > 119)
                    throw new InvalidDataException(name + "音符使用 0–127，CC 使用 64–119。");
                var signal = (binding.IsNote ? "Note " : "CC ") + binding.Number;
                if (!signals.Add(signal))
                    throw new InvalidDataException(name + signal + " 已用于其他映射。");
                if (!binding.IsNote && (binding.Step < 1 || binding.Step > (binding.IsRelative ? 8 : 127)))
                    throw new InvalidDataException(name + "相对步进使用 1–8，绝对步进使用 1–127。");
                if (!binding.IsNote && !binding.IsRelative && (binding.Initial < 0 || binding.Initial > 127))
                    throw new InvalidDataException(name + "初始值使用 0–127。");
                foreach (var target in binding.TargetIds())
                {
                    if (!IsTargetId(target) || !targets.Add(target))
                        throw new InvalidDataException(name + "控件 ID 无效或重复（" + target + "）。");
                }
            }
        }

        internal static bool IsTargetId(string target)
        {
            return target.Length <= 120 && Regex.IsMatch(target, @"^(d[0-3]|mx|p[12])@[^\s:|]+:[^\s|]+$");
        }
    }

    internal static class Chord
    {
        internal const Keys PauseKey = Keys.F8;
        internal const int ModifierMask = (int)(Keys.Control | Keys.Alt | Keys.Shift);

        internal static bool CanBind(int chord)
        {
            var key = (Keys)(chord & (int)Keys.KeyCode);
            return (chord & ~((int)Keys.KeyCode | ModifierMask)) == 0 && (int)key > 0 && (int)key <= 254 &&
                key != Keys.Escape && key != PauseKey && key != Keys.LWin && key != Keys.RWin &&
                key != Keys.Packet && !IsModifier(key);
        }

        internal static bool IsModifier(Keys key)
        {
            return key == Keys.ShiftKey || key == Keys.ControlKey || key == Keys.Menu ||
                key == Keys.LShiftKey || key == Keys.RShiftKey || key == Keys.LControlKey ||
                key == Keys.RControlKey || key == Keys.LMenu || key == Keys.RMenu;
        }

        internal static string Display(int chord)
        {
            if (chord == 0) return "—";
            var key = (Keys)(chord & (int)Keys.KeyCode);
            string label = key.ToString();
            if (key >= Keys.D0 && key <= Keys.D9) label = ((int)key - (int)Keys.D0).ToString();
            if (key == Keys.Oemcomma) label = ",";
            if (key == Keys.OemPeriod) label = ".";
            if (key == Keys.Space) label = "Space";
            return ((chord & (int)Keys.Control) != 0 ? "Ctrl+" : "") +
                ((chord & (int)Keys.Alt) != 0 ? "Alt+" : "") +
                ((chord & (int)Keys.Shift) != 0 ? "Shift+" : "") + label;
        }
    }

    internal sealed class MappingEngine
    {
        private sealed class Action
        {
            internal Binding Binding;
            internal int Direction;
            internal long Next;
        }

        private readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();
        private readonly Dictionary<int, Action> held = new Dictionary<int, Action>();
        private readonly Dictionary<string, int> positions;
        private readonly string positionPrefix;
        private readonly System.Action<int, int, int> send;
        private readonly int channel;

        internal MappingEngine(Profile profile, System.Action<int, int, int> send, Dictionary<string, int> positions = null)
        {
            this.send = send;
            this.positions = positions ?? new Dictionary<string, int>();
            channel = profile.Channel - 1;
            positionPrefix = profile.Output + "|" + channel + "|";
            foreach (var binding in profile.Bindings)
            {
                if (binding.Down != 0) actions.Add(binding.Down, new Action { Binding = binding, Direction = -1 });
                if (binding.Up != 0) actions.Add(binding.Up, new Action { Binding = binding, Direction = 1 });
            }
        }

        internal bool Press(int key, int modifiers, long now)
        {
            if (held.ContainsKey(key)) return true;
            Action action;
            if (!actions.TryGetValue(key | modifiers, out action)) return false;
            Move(action);
            action.Next = now + 300;
            held.Add(key, action);
            return true;
        }

        internal void Advance(long now, int modifiers)
        {
            foreach (var entry in held.ToArray())
            {
                if (entry.Value.Binding.IsNote) continue;
                Action action;
                if (!actions.TryGetValue(entry.Key | modifiers, out action) || !Object.ReferenceEquals(action, entry.Value))
                {
                    Release(entry.Key);
                    continue;
                }
                if (now >= entry.Value.Next)
                {
                    Move(entry.Value);
                    entry.Value.Next = now + 45;
                }
            }
        }

        internal void Release(int key)
        {
            Action action;
            if (!held.TryGetValue(key, out action)) return;
            held.Remove(key);
            if (action.Binding.IsNote) send(0x80 | channel, action.Binding.Number, 0);
        }

        internal void ReleaseAll()
        {
            Exception failure = null;
            foreach (var key in held.Keys.ToArray())
            {
                try { Release(key); }
                catch (InvalidOperationException error) { failure = error; }
            }
            if (failure != null) throw failure;
        }

        internal void Learn(Binding binding)
        {
            if (binding.IsNote)
            {
                send(0x90 | channel, binding.Number, 127);
                send(0x80 | channel, binding.Number, 0);
            }
            else if (binding.IsRelative)
            {
                for (int index = 0; index < 6; index++)
                    send(0xB0 | channel, binding.Number, index % 2 == 0 ? 1 : 127);
            }
            else send(0xB0 | channel, binding.Number, binding.Initial);
        }

        private void Move(Action action)
        {
            var binding = action.Binding;
            if (binding.IsNote)
            {
                send(0x90 | channel, binding.Number, 127);
                return;
            }
            int delta = action.Direction * binding.Step;
            if (binding.IsRelative)
            {
                send(0xB0 | channel, binding.Number, (delta + 128) % 128);
                return;
            }
            string position = positionPrefix + binding.Number + "|" + binding.Initial;
            int previous;
            if (!positions.TryGetValue(position, out previous)) previous = binding.Initial;
            int next = Math.Max(0, Math.Min(127, previous + delta));
            if (next != previous) send(0xB0 | channel, binding.Number, next);
            positions[position] = next;
        }
    }

    internal sealed class MidiOutput : IDisposable
    {
        internal sealed class Port
        {
            internal uint Index;
            internal string Name;
            public override string ToString() { return Name; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Caps
        {
            internal ushort Manufacturer, Product;
            internal uint Version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
            internal ushort Technology, Voices, Notes, ChannelMask;
            internal uint Support;
        }

        private IntPtr handle;

        internal static List<Port> Ports()
        {
            var ports = new List<Port>();
            uint total = midiOutGetNumDevs();
            for (uint index = 0; index < total; index++)
            {
                Caps caps;
                if (midiOutGetDevCapsW((UIntPtr)index, out caps, (uint)Marshal.SizeOf(typeof(Caps))) == 0 &&
                    caps.Technology == 1)
                    ports.Add(new Port { Index = index, Name = caps.Name });
            }
            return ports;
        }

        internal MidiOutput(uint index)
        {
            Check(midiOutOpen(out handle, index, IntPtr.Zero, IntPtr.Zero, 0), "打开 MIDI 输出");
        }

        internal void Send(int status, int number, int value)
        {
            Check(midiOutShortMsg(handle, (uint)(status | number << 8 | value << 16)), "发送 MIDI");
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            midiOutClose(handle);
            handle = IntPtr.Zero;
        }

        private static void Check(uint result, string action)
        {
            if (result == 0) return;
            var message = new StringBuilder(256);
            midiOutGetErrorTextW(result, message, (uint)message.Capacity);
            throw new InvalidOperationException(action + "失败（" + result + "）" +
                (message.Length == 0 ? "。" : "：" + message));
        }

        [DllImport("winmm.dll")] private static extern uint midiOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint midiOutGetDevCapsW(UIntPtr device, out Caps caps, uint size);
        [DllImport("winmm.dll")]
        private static extern uint midiOutOpen(out IntPtr handle, uint device, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint midiOutShortMsg(IntPtr handle, uint message);
        [DllImport("winmm.dll")] private static extern uint midiOutClose(IntPtr handle);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint midiOutGetErrorTextW(uint error, StringBuilder text, uint size);
    }

    internal sealed class StudioKeyboard : IDisposable
    {
        private delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
        private readonly Hook callback;
        private readonly Func<int, bool, bool, bool> onKey;
        private IntPtr handle, foreground;
        private bool studioFocused;

        internal StudioKeyboard(Func<int, bool, bool, bool> onKey)
        {
            this.onKey = onKey;
            callback = Receive;
            handle = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("无法启用键盘监听（" + Marshal.GetLastWin32Error() + "）。");
        }

        internal bool IsStudioFocused()
        {
            var window = GetForegroundWindow();
            if (window != foreground)
            {
                foreground = window;
                studioFocused = false;
                if (window != IntPtr.Zero)
                {
                    uint pid;
                    GetWindowThreadProcessId(window, out pid);
                    try
                    {
                        using (var process = Process.GetProcessById((int)pid))
                            studioFocused = String.Equals(process.ProcessName, "B2B Studio", StringComparison.OrdinalIgnoreCase);
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
            }
            return studioFocused;
        }

        internal static int Modifiers()
        {
            return (IsDown(Keys.ControlKey) ? (int)Keys.Control : 0) |
                (IsDown(Keys.Menu) ? (int)Keys.Alt : 0) |
                (IsDown(Keys.ShiftKey) ? (int)Keys.Shift : 0);
        }

        internal static bool IsDown(Keys key)
        {
            return (GetAsyncKeyState((int)key) & 0x8000) != 0;
        }

        private IntPtr Receive(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0)
            {
                int kind = message.ToInt32();
                bool down = kind == 0x100 || kind == 0x104;
                bool up = kind == 0x101 || kind == 0x105;
                const int injected = 0x10;
                if ((down || up) && (Marshal.ReadInt32(data, 8) & injected) == 0 &&
                    onKey(Marshal.ReadInt32(data), down, IsStudioFocused()))
                    return new IntPtr(1);
            }
            return CallNextHookEx(handle, code, message, data);
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero) UnhookWindowsHookEx(handle);
            handle = IntPtr.Zero;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, Hook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    }
}
