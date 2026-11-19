using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace B2BKeys
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool first;
            using (var instance = new System.Threading.Mutex(true, @"Local\B2B Keyboard Mapper", out first))
            {
                if (!first)
                {
                    MessageBox.Show("键盘映射工具已打开。", "B2B Keyboard Mapper");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MapperWindow());
            }
        }
    }

    internal sealed class MapperWindow : Form
    {
        private static readonly Color Surface = Color.FromArgb(24, 28, 36);
        private static readonly Color Ink = Color.FromArgb(237, 240, 246);
        private static readonly Color Muted = Color.FromArgb(179, 191, 208);
        private static readonly Color Accent = Color.FromArgb(108, 221, 193);
        private static readonly Color Alert = Color.FromArgb(255, 188, 126);
        private static readonly string[] Modes = { "按钮", "旋钮", "推子" };
        private static readonly string[] ModeIds = { "note", "relative", "absolute" };

        private readonly ComboBox output = new ComboBox { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox inputName = new TextBox { Width = 145 };
        private readonly NumericUpDown channel = new NumericUpDown { Minimum = 1, Maximum = 16, Width = 52 };
        private readonly MappingGrid grid = new MappingGrid();
        private readonly Label status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Button toggle;
        private readonly FlowLayoutPanel ports = new FlowLayoutPanel();
        private readonly FlowLayoutPanel toolbar = new FlowLayoutPanel();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Timer timer = new Timer { Interval = 15 };
        private readonly HashSet<int> swallowed = new HashSet<int>();
        private readonly Dictionary<string, int> positions = new Dictionary<string, int>();
        private readonly ToolTip tips = new ToolTip();
        private StudioKeyboard keyboard;
        private MidiOutput midi;
        private MappingEngine engine;
        private DataGridViewCell capturing;
        private System.Threading.CancellationTokenSource learning;
        private bool active, prepared;
        private string preferredOutput = Profile.DefaultPort;

        internal MapperWindow()
        {
            Text = "B2B Keyboard Mapper";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1080, 600);
            MinimumSize = new Size(950, 510);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(16, 19, 26);
            ForeColor = Ink;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 12, 20, 12), ColumnCount = 1, RowCount = 6 };
            foreach (var size in new[] { 43, 25, 48, 39 })
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, size));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 41));
            Controls.Add(layout);
            layout.Controls.Add(new Label
            {
                Text = Text, ForeColor = Ink, Font = new Font("Segoe UI", 22F, FontStyle.Bold),
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            layout.Controls.Add(new Label
            {
                Text = "双击键位录入按键或组合键。启用后只在 B2B Studio 前台生效，F8 暂停或恢复。",
                Dock = DockStyle.Fill, ForeColor = Muted
            }, 0, 1);

            ports.Dock = DockStyle.Fill;
            ports.WrapContents = false;
            layout.Controls.Add(ports, 0, 2);
            AddLabel(ports, "MIDI 输出");
            ports.Controls.Add(output);
            AddButton(ports, "刷新", delegate { RefreshPorts(); });
            AddLabel(ports, "通道");
            ports.Controls.Add(channel);
            AddLabel(ports, "Studio 输入名（可选）");
            ports.Controls.Add(inputName);
            tips.SetToolTip(inputName, "输入端口名称与输出一致时留空即可。此名称用于识别学习结果和导出 Studio 预设。");

            toolbar.Dock = DockStyle.Fill;
            toolbar.WrapContents = false;
            layout.Controls.Add(toolbar, 0, 3);
            AddButton(toolbar, "新增映射", AddRow);
            AddButton(toolbar, "删除所选", DeleteRow);
            AddButton(toolbar, "保存键位", Save);
            AddButton(toolbar, "导入键位", ImportKeys);
            AddButton(toolbar, "导出键位", ExportKeys);
            AddButton(toolbar, "导出 Studio 预设", ExportStudio);
            AddButton(toolbar, "绑定选中控件", Learn);

            SetupGrid();
            layout.Controls.Add(grid, 0, 4);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Controls.Add(status, 0, 0);
            toggle = AddButton(footer, "启用映射", ToggleFromWindow, true);
            layout.Controls.Add(footer, 0, 5);

            output.SelectionChangeCommitted += delegate
            {
                var port = output.SelectedItem as MidiOutput.Port;
                if (port != null) preferredOutput = port.Name;
            };
            timer.Tick += delegate { Tick(); };
            Shown += delegate { OpenKeyboard(); };
            FormClosing += delegate
            {
                if (learning != null) learning.Cancel();
                timer.Stop();
                Pause();
                if (keyboard != null) keyboard.Dispose();
                tips.Dispose();
            };
            LoadProfile();
        }

        private static void AddLabel(Control parent, string text)
        {
            parent.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = Muted, Margin = new Padding(10, 7, 2, 0) });
        }

        private static Button AddButton(Control parent, string text, Action click, bool primary = false)
        {
            var button = new Button
            {
                Text = text, AutoSize = true, Height = 30, FlatStyle = FlatStyle.Flat,
                Margin = new Padding(3, 1, 6, 2), MinimumSize = new Size(0, 29), Padding = new Padding(6, 0, 6, 0),
                BackColor = primary ? Accent : Surface, ForeColor = primary ? Color.FromArgb(10, 34, 30) : Ink
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += delegate { click(); };
            parent.Controls.Add(button);
            return button;
        }

        private void SetupGrid()
        {
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Surface;
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = Color.FromArgb(46, 55, 69);
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(37, 45, 58);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
            grid.ColumnHeadersHeight = 34;
            grid.DefaultCellStyle.BackColor = Surface;
            grid.DefaultCellStyle.ForeColor = Ink;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(43, 73, 85);
            grid.DefaultCellStyle.SelectionForeColor = Ink;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(28, 33, 42);
            grid.RowTemplate.Height = 30;
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.MultiSelect = false;
            grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

            AddColumn("名称", 170);
            var mode = new DataGridViewComboBoxColumn { HeaderText = "类型", Width = 106, FlatStyle = FlatStyle.Flat };
            mode.Items.AddRange(Modes);
            grid.Columns.Add(mode);
            AddColumn("按下 / 减少", 108, true);
            AddColumn("增加", 108, true);
            AddColumn("Note / CC", 75);
            AddColumn("步进", 58);
            AddColumn("初始值", 72);
            var targets = AddColumn("Studio 控件 ID（可选）", 195);
            targets.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            targets.MinimumWidth = 195;
            targets.ToolTipText = "学习成功后自动填入并保存，用于导出 Studio 预设。多个 ID 以逗号分隔。";
            foreach (DataGridViewColumn column in grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs args)
            {
                if (args.RowIndex >= 0 && (args.ColumnIndex == 2 || args.ColumnIndex == 3))
                    CaptureChord(grid.Rows[args.RowIndex].Cells[args.ColumnIndex]);
            };
            grid.HandleKeys = HandleGridKey;
            grid.Leave += delegate { EndCapture(); };
            grid.CurrentCellChanged += delegate
            {
                if (capturing != null && grid.CurrentCell != capturing) EndCapture();
            };
            grid.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs args)
            {
                if (args.RowIndex >= 0 && args.ColumnIndex == 1) UpdateRow(grid.Rows[args.RowIndex]);
            };
            grid.CurrentCellDirtyStateChanged += delegate
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewComboBoxCell)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
        }

        private DataGridViewColumn AddColumn(string title, int width, bool readOnly = false)
        {
            var column = new DataGridViewTextBoxColumn { HeaderText = title, Width = width, ReadOnly = readOnly };
            grid.Columns.Add(column);
            return column;
        }

        private void LoadProfile()
        {
            try
            {
                Fill(File.Exists(Profile.FilePath) ? Profile.Load(Profile.FilePath) : Profile.Defaults());
                SetStatus(output.Items.Count == 0 ? "找不到 MIDI 输出。请先创建 B2B Keys 虚拟端口，再点刷新。" : "已暂停，点击「启用映射」开始。");
            }
            catch (Exception error)
            {
                Fill(Profile.Defaults());
                SetStatus("配置读取失败，已载入示例。 " + error.Message, true);
            }
        }

        private void Fill(Profile profile)
        {
            preferredOutput = String.IsNullOrWhiteSpace(profile.Output) ? Profile.DefaultPort : profile.Output;
            inputName.Text = profile.Input ?? "";
            channel.Value = profile.Channel;
            grid.Rows.Clear();
            foreach (var binding in profile.Bindings) FillRow(binding);
            RefreshPorts();
        }

        private void FillRow(Binding binding)
        {
            int mode = Array.IndexOf(ModeIds, binding.Mode);
            var index = grid.Rows.Add(binding.Name, Modes[mode], Chord.Display(binding.Down), Chord.Display(binding.Up),
                binding.Number, binding.Step, binding.Initial, binding.Targets);
            var row = grid.Rows[index];
            row.Cells[2].Tag = binding.Down;
            row.Cells[3].Tag = binding.Up;
            UpdateRow(row);
        }

        private static void UpdateRow(DataGridViewRow row)
        {
            var mode = Convert.ToString(row.Cells[1].Value);
            row.Cells[3].ReadOnly = true;
            row.Cells[5].ReadOnly = mode == Modes[0];
            row.Cells[6].ReadOnly = mode != Modes[2];
            foreach (var index in new[] { 3, 5, 6 })
            {
                bool available = index == 3 ? mode != Modes[0] : !row.Cells[index].ReadOnly;
                row.Cells[index].Style.ForeColor = available ? Ink : Color.FromArgb(119, 132, 151);
            }
        }

        private void AddRow()
        {
            var used = new HashSet<int>(grid.Rows.Cast<DataGridViewRow>()
                .Where(row => Convert.ToString(row.Cells[1].Value) == Modes[0])
                .Select(row => ToInt(row.Cells[4].Value, -1)));
            int number = Enumerable.Range(0, 128).FirstOrDefault(candidate => !used.Contains(candidate));
            if (used.Contains(number))
            {
                SetStatus("Note 编号已用完。", true);
                return;
            }
            FillRow(new Binding { Name = "新映射", Mode = "note", Number = number, Step = 2, Initial = 64 });
            grid.CurrentCell = grid.Rows[grid.Rows.Count - 1].Cells[0];
        }

        private void DeleteRow()
        {
            if (grid.CurrentRow != null) grid.Rows.Remove(grid.CurrentRow);
        }

        private void CaptureChord(DataGridViewCell cell)
        {
            if (cell.ColumnIndex == 3 && Convert.ToString(cell.OwningRow.Cells[1].Value) == Modes[0]) return;
            grid.CurrentCell = cell;
            capturing = cell;
            cell.Style.SelectionForeColor = Accent;
            SetStatus("按下要绑定的键或组合键。Esc 取消，Delete 清除。");
        }

        private bool HandleGridKey(Keys key)
        {
            if (capturing == null)
            {
                var cell = grid.CurrentCell;
                if (cell == null || (cell.ColumnIndex != 2 && cell.ColumnIndex != 3) || (key != Keys.Enter && key != Keys.F2))
                    return false;
                CaptureChord(cell);
                return true;
            }
            if (key == Keys.Escape) { EndCapture(); return true; }
            if (key != Keys.Delete && !Chord.CanBind((int)key)) return true;
            var value = key == Keys.Delete ? 0 : (int)key;
            capturing.Tag = value;
            capturing.Value = Chord.Display(value);
            EndCapture();
            SetStatus(value == 0 ? "键位已清除。" : "已录入 " + Chord.Display(value) + "。");
            return true;
        }

        private void EndCapture()
        {
            if (capturing == null) return;
            capturing.Style.SelectionForeColor = Color.Empty;
            capturing = null;
            SetStatus("已退出键位录入。");
        }

        private Profile ReadGrid()
        {
            if (!grid.EndEdit()) throw new InvalidDataException("请完成当前单元格的编辑。");
            var bindings = new List<Binding>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                var mode = Array.IndexOf(Modes, Convert.ToString(row.Cells[1].Value));
                bindings.Add(new Binding
                {
                    Name = Convert.ToString(row.Cells[0].Value).Trim(), Mode = mode < 0 ? "" : ModeIds[mode],
                    Down = ToInt(row.Cells[2].Tag, 0), Up = mode == 0 ? 0 : ToInt(row.Cells[3].Tag, 0),
                    Number = ToInt(row.Cells[4].Value, -1), Step = ToInt(row.Cells[5].Value, -1),
                    Initial = ToInt(row.Cells[6].Value, -1), Targets = Convert.ToString(row.Cells[7].Value).Trim()
                });
            }
            var profile = new Profile
            {
                Version = 1, Bindings = bindings, Output = preferredOutput, Input = inputName.Text.Trim(), Channel = (int)channel.Value
            };
            profile.Validate();
            return profile;
        }

        private static int ToInt(object value, int fallback)
        {
            int number;
            return Int32.TryParse(Convert.ToString(value), out number) ? number : fallback;
        }

        private void RefreshPorts()
        {
            output.Items.Clear();
            foreach (var port in MidiOutput.Ports()) output.Items.Add(port);
            output.SelectedIndex = -1;
            foreach (MidiOutput.Port port in output.Items)
            {
                if (port.Name != preferredOutput) continue;
                output.SelectedItem = port;
                break;
            }
            if (output.Items.Count == 0) SetStatus("找不到 MIDI 输出。请先创建 B2B Keys 虚拟端口，再点刷新。");
            else if (output.SelectedIndex < 0) SetStatus("请选择 MIDI 输出端口。");
            else SetStatus("已选择 " + preferredOutput + "。");
        }

        private void Save()
        {
            ShowErrors(delegate
            {
                ReadGrid().Save(Profile.FilePath);
                SetStatus("键位已保存。");
            });
        }

        private void ImportKeys()
        {
            using (var dialog = new OpenFileDialog { Filter = "键位配置 (*.json)|*.json" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                ShowErrors(delegate
                {
                    Fill(Profile.Load(dialog.FileName));
                    SetStatus("已载入键位。点击「保存键位」或「启用映射」以保留。");
                });
            }
        }

        private void ExportKeys()
        {
            WithSaveDialog("键位配置 (*.json)|*.json", "B2B Keys.json",
                delegate(string path) { ReadGrid().Save(path); SetStatus("键位配置已导出。"); });
        }

        private void ExportStudio()
        {
            WithSaveDialog("Studio MIDI 预设 (*.json)|*.json", "B2B_Keys.b2bmidi.json", delegate(string path)
            {
                var profile = ReadGrid();
                profile.ExportPreset(path, profile.StudioPort);
                SetStatus("预设已导出。可在 Studio 的 MIDI ▾ → Import 中载入。");
            }, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        }

        private void WithSaveDialog(string filter, string name, Action<string> save, string initialDirectory = "")
        {
            using (var dialog = new SaveFileDialog
            {
                Filter = filter, FileName = name, DefaultExt = "json", InitialDirectory = initialDirectory, RestoreDirectory = true
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK) ShowErrors(delegate { save(dialog.FileName); });
            }
        }

        private static MidiOutput OpenOutput(string outputName)
        {
            var port = MidiOutput.Ports().FirstOrDefault(item => item.Name == outputName);
            if (port == null) throw new InvalidOperationException("找不到「" + outputName + "」MIDI 输出。请创建端口或刷新后选择。");
            return new MidiOutput(port.Index);
        }

        private async void Learn()
        {
            if (grid.CurrentRow == null || active || learning != null) return;
            var row = grid.CurrentRow;
            var request = new System.Threading.CancellationTokenSource();
            learning = request;
            try
            {
                var profile = ReadGrid();
                var binding = profile.Bindings[row.Index];
                var studio = new StudioLearning(profile, binding);
                SetEditorEnabled(false);
                toggle.Enabled = false;
                SetStatus("正在读取 Studio 的 MIDI 学习状态…");
                await studio.CaptureAsync(request.Token);
                request.Token.ThrowIfCancellationRequested();

                using (var device = OpenOutput(profile.Output))
                    new MappingEngine(profile, device.Send).Learn(binding);
                SetStatus("已发送「" + binding.Name + "」的学习信号，正在等待 Studio 确认…");
                string target = await Task.Run(() => studio.WaitForTargetAsync(request.Token), request.Token);
                request.Token.ThrowIfCancellationRequested();

                var owner = profile.Bindings.FirstOrDefault(item => item != binding && item.TargetIds().Contains(target));
                if (owner != null)
                    throw new InvalidOperationException("Studio 已绑定「" + target + "」，但该 ID 已用于「" + owner.Name + "」。");
                if (!binding.TargetIds().Contains(target))
                    binding.Targets = String.Join(", ", binding.TargetIds().Concat(new[] { target }));

                try { profile.Save(Profile.FilePath); }
                catch (Exception error)
                {
                    throw new InvalidOperationException("Studio 已绑定「" + target + "」，但保存键位失败。" + error.Message, error);
                }
                row.Cells[7].Value = binding.Targets;
                SetStatus("已保存「" + binding.Name + "」的 Studio 控件 ID「" + target + "」。");
            }
            catch (Exception error)
            {
                if (!request.IsCancellationRequested) SetStatus(error.Message, true);
            }
            finally
            {
                learning = null;
                if (!request.IsCancellationRequested)
                {
                    SetEditorEnabled(true);
                    toggle.Enabled = true;
                }
                request.Dispose();
            }
        }

        private void OpenKeyboard()
        {
            ShowErrors(delegate
            {
                keyboard = new StudioKeyboard(OnKey);
                timer.Start();
            });
        }

        private void ToggleFromWindow()
        {
            if (learning != null) return;
            if (active) { Pause(); return; }
            ShowErrors(delegate
            {
                if (keyboard == null) throw new InvalidOperationException("键盘监听尚未启动，请重新打开工具。");
                var profile = ReadGrid();
                Start(profile);
                try
                {
                    profile.Save(Profile.FilePath);
                    prepared = true;
                }
                catch
                {
                    Pause();
                    throw;
                }
            });
        }

        private void Start(Profile profile)
        {
            if (profile.Bindings.Count == 0) throw new InvalidOperationException("请先添加至少一条映射。");
            midi = OpenOutput(profile.Output);
            engine = new MappingEngine(profile, midi.Send, positions);
            active = true;
            SetEditorEnabled(false);
            SetStatus("映射已启用 · 切回 B2B Studio 即可使用。");
        }

        private void SetEditorEnabled(bool enabled)
        {
            grid.Enabled = ports.Enabled = toolbar.Enabled = enabled;
            toggle.Text = active ? "暂停映射" : "启用映射";
        }

        private void Pause()
        {
            if (!active) return;
            active = false;
            try
            {
                engine.ReleaseAll();
                SetStatus("映射已暂停。");
            }
            catch (InvalidOperationException error) { SetStatus(error.Message, true); }
            finally
            {
                midi.Dispose();
                midi = null;
                engine = null;
                SetEditorEnabled(true);
            }
        }

        private void Tick()
        {
            if (!active) return;
            try
            {
                if (keyboard.IsStudioFocused()) engine.Advance(clock.ElapsedMilliseconds, StudioKeyboard.Modifiers());
                else engine.ReleaseAll();
            }
            catch (InvalidOperationException error) { FailSending(error); }
        }

        private bool OnKey(int key, bool down, bool focused)
        {
            try
            {
                if (!down)
                {
                    bool captured = swallowed.Remove(key);
                    if (active) engine.Release(key);
                    return captured && focused;
                }
                if (!focused || StudioKeyboard.IsDown(Keys.LWin) || StudioKeyboard.IsDown(Keys.RWin)) return false;
                if (swallowed.Contains(key)) return true;
                if (StudioKeyboard.IsDown((Keys)key)) return false;
                int modifiers = StudioKeyboard.Modifiers();
                if (key == (int)Chord.PauseKey && modifiers == 0 && learning == null && (active || prepared))
                {
                    swallowed.Add(key);
                    BeginInvoke(new Action(ToggleFromWindow));
                    return true;
                }
                if (!active || !engine.Press(key, modifiers, clock.ElapsedMilliseconds)) return false;
                swallowed.Add(key);
                return true;
            }
            catch (InvalidOperationException error) { FailSending(error); return false; }
        }

        private void FailSending(InvalidOperationException error)
        {
            Pause();
            SetStatus("映射已暂停。 " + error.Message, true);
        }

        private void ShowErrors(Action action)
        {
            try { action(); }
            catch (Exception error) { SetStatus(error.Message, true); }
        }

        private void SetStatus(string text, bool warning = false)
        {
            status.ForeColor = warning ? Alert : active ? Accent : Muted;
            status.Text = text;
            tips.SetToolTip(status, text);
        }
    }

    internal sealed class MappingGrid : DataGridView
    {
        internal Func<Keys, bool> HandleKeys;
        protected override bool ProcessCmdKey(ref Message message, Keys key)
        {
            return (HandleKeys != null && HandleKeys(key)) || base.ProcessCmdKey(ref message, key);
        }
    }
}
