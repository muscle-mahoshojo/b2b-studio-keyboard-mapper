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
        internal string Signal { get { return (IsNote ? "Note " : "CC ") + Number; } }

        internal string MidiSource(string port, int channel)
        {
            return String.Format("{0}|{1}|{2}|{3}", port, IsNote ? "note" : "cc", channel - 1, Number);
        }

        internal IEnumerable<string> TargetIds()
        {
            return (Targets ?? "").Split(',').Select(target => target.Trim()).Where(target => target.Length > 0);
        }
    }

    internal enum MappingScope { Common = -1, Left = 0, Right = 1 }

    public sealed class DeckConfiguration
    {
        public string Name { get; set; }
        public List<Binding> Bindings { get; set; }
    }

    public sealed class DeckGroup
    {
        public int SwitchKey { get; set; }
        public int DefaultDeck { get; set; }
        public List<DeckConfiguration> Decks { get; set; }
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
        public DeckGroup Left { get; set; }
        public DeckGroup Right { get; set; }

        internal string StudioPort { get { return String.IsNullOrWhiteSpace(Input) ? Output : Input; } }

        internal DeckGroup Group(MappingScope scope)
        {
            return scope == MappingScope.Left ? Left : Right;
        }

        internal static string GroupName(MappingScope scope)
        {
            return scope == MappingScope.Left ? "左组" : "右组";
        }

        internal IEnumerable<Binding> AllBindings()
        {
            var bindings = Bindings.AsEnumerable();
            if (Version == 2)
                bindings = bindings.Concat(Left.Decks.Concat(Right.Decks).SelectMany(deck => deck.Bindings));
            return bindings;
        }

        internal void Upgrade()
        {
            if (Version != 1) return;
            Left = NewGroup(Keys.F2);
            Right = NewGroup(Keys.F11);
            Version = 2;
        }

        private DeckGroup NewGroup(Keys shortcut)
        {
            int key = (int)shortcut;
            return new DeckGroup
            {
                SwitchKey = Bindings.Any(binding => binding.Down == key || binding.Up == key) ? 0 : key,
                Decks = new List<DeckConfiguration>()
            };
        }

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
            profile.Upgrade();
            return profile;
        }

        internal void Save(string path)
        {
            Validate();
            Upgrade();
            string serialized = Json.Serialize(this);
            if (Encoding.UTF8.GetByteCount(serialized) + Environment.NewLine.Length > Json.MaxJsonLength)
                throw new InvalidDataException("配置文件过大。");
            Write(path, serialized);
        }

        internal void SaveLearnedTarget(Binding binding, string target, string path)
        {
            var all = AllBindings().ToArray();
            var owner = all.FirstOrDefault(item => item.Signal != binding.Signal && item.TargetIds().Contains(target));
            if (owner != null)
                throw new InvalidOperationException("Studio 已绑定「" + target + "」，但该 ID 已用于「" + owner.Name + "」。请用「复用映射」。");

            var linked = all.Where(item => item.Signal == binding.Signal).ToArray();
            var previous = linked.Select(item => item.Targets).ToArray();
            foreach (var item in linked)
            {
                if (!item.TargetIds().Contains(target))
                    item.Targets = String.Join(", ", item.TargetIds().Concat(new[] { target }));
            }
            try { Save(path); }
            catch (Exception error)
            {
                for (int index = 0; index < linked.Length; index++) linked[index].Targets = previous[index];
                throw new InvalidOperationException("Studio 已绑定「" + target + "」，但保存键位失败。" + error.Message, error);
            }
        }

        internal void ExportPreset(string path, string port)
        {
            Validate();
            if (String.IsNullOrWhiteSpace(port) || port.Contains("|"))
                throw new InvalidDataException("请输入 Studio 里看到的 MIDI 输入端口名称。");
            var bindings = new Dictionary<string, object>();
            foreach (var binding in AllBindings())
            {
                var src = binding.MidiSource(port, Channel);
                foreach (var target in binding.TargetIds())
                {
                    var mapping = new Dictionary<string, string> { { "src", src } };
                    if (binding.IsRelative) mapping.Add("rel", "2c");
                    if (!bindings.ContainsKey(target)) bindings.Add(target, mapping);
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
            if ((Version != 1 && Version != 2) || Channel < 1 || Channel > 16 || Bindings == null)
                throw new InvalidDataException("无法读取此版本的配置或 MIDI 通道超出范围。");
            var signals = new Dictionary<string, Binding>();
            var targets = new Dictionary<string, Binding>();
            var commonKeys = ValidateBindings(Bindings, "共用映射", signals, targets);
            if (Version == 1) return;

            var leftKeys = ValidateGroup(Left, "左组", signals, targets);
            var rightKeys = ValidateGroup(Right, "右组", signals, targets);
            CheckKeyOverlap(leftKeys, rightKeys, "左组和右组");
            leftKeys.UnionWith(rightKeys);
            CheckKeyOverlap(leftKeys, commonKeys, "Deck 配置和共用映射");
            commonKeys.UnionWith(leftKeys);
            foreach (int shortcut in new[] { Left.SwitchKey, Right.SwitchKey }.Where(key => key != 0))
            {
                if (!commonKeys.Add(shortcut))
                    throw new InvalidDataException("切换键「" + Chord.Display(shortcut) + "」与映射或另一组的切换键冲突。");
            }
        }

        private HashSet<int> ValidateGroup(DeckGroup group, string name, Dictionary<string, Binding> signals,
            Dictionary<string, Binding> targets)
        {
            if (group == null || group.Decks == null || group.DefaultDeck < 0
                || group.DefaultDeck >= Math.Max(1, group.Decks.Count))
                throw new InvalidDataException(name + "的默认 Deck 或配置无效。");
            if ((group.SwitchKey == 0 && group.Decks.Count > 1)
                || (group.SwitchKey != 0 && !Chord.CanBind(group.SwitchKey)))
                throw new InvalidDataException("请为" + name + "设置有效的切换键。");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<int>();
            foreach (var deck in group.Decks)
            {
                if (deck == null || deck.Bindings == null || String.IsNullOrWhiteSpace(deck.Name) || !names.Add(deck.Name.Trim()))
                    throw new InvalidDataException(name + "的 Deck 名称为空或重复。");
                keys.UnionWith(ValidateBindings(deck.Bindings, name + " / " + deck.Name, signals, targets));
            }
            return keys;
        }

        private static void CheckKeyOverlap(HashSet<int> first, HashSet<int> second, string name)
        {
            int conflict = first.FirstOrDefault(second.Contains);
            if (conflict != 0)
                throw new InvalidDataException(name + "都使用了「" + Chord.Display(conflict) + "」，请更改其中一个键位。");
        }

        private HashSet<int> ValidateBindings(List<Binding> bindings, string scope,
            Dictionary<string, Binding> signals, Dictionary<string, Binding> targets)
        {
            var keys = new HashSet<int>();
            foreach (var binding in bindings)
            {
                if (binding == null || String.IsNullOrWhiteSpace(binding.Name))
                    throw new InvalidDataException("请为「" + scope + "」的每条映射填写名称。");
                var name = scope + " / " + binding.Name + "：";
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
                if (!binding.IsNote && (binding.Step < 1 || binding.Step > (binding.IsRelative ? 8 : 127)))
                    throw new InvalidDataException(name + "相对步进使用 1–8，绝对步进使用 1–127。");
                if (!binding.IsNote && !binding.IsRelative && (binding.Initial < 0 || binding.Initial > 127))
                    throw new InvalidDataException(name + "初始值使用 0–127。");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                Binding previous;
                foreach (var target in binding.TargetIds())
                {
                    if (!IsTargetId(target) || !ids.Add(target))
                        throw new InvalidDataException(name + "控件 ID 无效或重复（" + target + "）。");
                    if (targets.TryGetValue(target, out previous) && (Version == 1 || previous.Signal != binding.Signal))
                        throw new InvalidDataException(name + "控件「" + target + "」已使用 " + previous.Signal + "。请用「复用映射」。");
                    targets[target] = binding;
                }
                if (signals.TryGetValue(binding.Signal, out previous)
                    && (Version == 1 || ids.Count == 0 || !ids.SetEquals(previous.TargetIds()) || binding.Mode != previous.Mode
                        || (binding.Mode == "absolute" && binding.Initial != previous.Initial)))
                    throw new InvalidDataException(name + binding.Signal + " 已用于其他映射。复用时须使用相同的控件 ID、类型和推子初始值。");
                signals[binding.Signal] = binding;
            }
            return keys;
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
            internal MappingScope Scope;
            internal int Modifiers;
            internal int Direction;
            internal long Next;
        }

        private readonly Dictionary<int, Action> common;
        private readonly List<Dictionary<int, Action>>[] decks;
        private readonly int[] selected = { -1, -1 };
        private readonly int[] switchKeys = new int[2];
        private readonly Dictionary<int, Action> held = new Dictionary<int, Action>();
        private readonly Dictionary<int, int> noteHolds = new Dictionary<int, int>();
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
            common = BuildActions(profile.Bindings, MappingScope.Common);
            decks = new[] { new List<Dictionary<int, Action>>(), new List<Dictionary<int, Action>>() };
            if (profile.Version == 2)
            {
                foreach (var scope in new[] { MappingScope.Left, MappingScope.Right })
                {
                    int side = (int)scope;
                    var group = profile.Group(scope);
                    decks[side].AddRange(group.Decks.Select(deck => BuildActions(deck.Bindings, scope)));
                    if (group.Decks.Count > 0) selected[side] = group.DefaultDeck;
                    if (group.Decks.Count > 1) switchKeys[side] = group.SwitchKey;
                }
            }
        }

        private static Dictionary<int, Action> BuildActions(IEnumerable<Binding> bindings, MappingScope scope)
        {
            var actions = new Dictionary<int, Action>();
            foreach (var binding in bindings)
            {
                if (binding.Down != 0) actions.Add(binding.Down, new Action { Binding = binding, Scope = scope,
                    Modifiers = binding.Down & Chord.ModifierMask, Direction = -1 });
                if (binding.Up != 0) actions.Add(binding.Up, new Action { Binding = binding, Scope = scope,
                    Modifiers = binding.Up & Chord.ModifierMask, Direction = 1 });
            }
            return actions;
        }

        internal int CurrentDeck(MappingScope scope) { return selected[(int)scope]; }

        internal void SelectDeck(MappingScope scope, int index)
        {
            int side = (int)scope;
            if (index == selected[side]) return;
            ReleaseKeys(held.Where(entry => entry.Value.Scope == scope).Select(entry => entry.Key).ToArray());
            selected[side] = index;
        }

        internal bool TrySwitchDeck(int shortcut, out MappingScope scope)
        {
            int side = Array.IndexOf(switchKeys, shortcut);
            scope = (MappingScope)side;
            if (shortcut == 0 || side < 0) return false;
            SelectDeck(scope, (selected[side] + 1) % decks[side].Count);
            return true;
        }

        private bool TryGetAction(int chord, out Action action)
        {
            return common.TryGetValue(chord, out action)
                || (selected[0] >= 0 && decks[0][selected[0]].TryGetValue(chord, out action))
                || (selected[1] >= 0 && decks[1][selected[1]].TryGetValue(chord, out action));
        }

        internal bool Press(int key, int modifiers, long now)
        {
            if (held.ContainsKey(key)) return true;
            Action action;
            if (!TryGetAction(key | modifiers, out action)) return false;
            if (action.Binding.IsNote)
            {
                int number = action.Binding.Number;
                int count;
                if (!noteHolds.TryGetValue(number, out count)) send(0x90 | channel, number, 127);
                noteHolds[number] = count + 1;
            }
            else Move(action);
            action.Next = now + 300;
            held.Add(key, action);
            return true;
        }

        internal void Advance(long now, int modifiers)
        {
            foreach (var entry in held.ToArray())
            {
                if (entry.Value.Binding.IsNote) continue;
                if ((modifiers & entry.Value.Modifiers) != entry.Value.Modifiers)
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
            if (!action.Binding.IsNote) return;
            int number = action.Binding.Number;
            if (noteHolds[number] > 1) noteHolds[number]--;
            else
            {
                noteHolds.Remove(number);
                send(0x80 | channel, number, 0);
            }
        }

        internal void ReleaseAll()
        {
            ReleaseKeys(held.Keys.ToArray());
        }

        private void ReleaseKeys(IEnumerable<int> keys)
        {
            Exception failure = null;
            foreach (var key in keys)
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
