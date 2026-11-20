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
        private static readonly string[] Modes = { "按钮", "相对旋钮", "绝对cc" };
        private static readonly string[] ModeIds = { "note", "relative", "absolute" };

        private readonly ComboBox output = new ComboBox { Width = 195, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox inputName = new TextBox { Width = 145 };
        private readonly NumericUpDown channel = new NumericUpDown { Minimum = 1, Maximum = 16, Width = 56 };
        private readonly MappingGrid grid = new MappingGrid();
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
        private readonly Button toggle;
        private readonly FlowLayoutPanel ports = NewFlow();
        private readonly FlowLayoutPanel deckEditor = NewFlow();
        private readonly ComboBox scopeSelector = new ComboBox { Width = 98, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox deckSelector = new ComboBox { Width = 132, DropDownWidth = 245, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly MappingIndicator mappingStatus = new MappingIndicator();
        private readonly DeckCard leftStatus, rightStatus;
        private readonly CheckBox overlayToggle = new CheckBox { Text = "显示悬浮窗", Checked = true, AutoSize = true };
        private readonly Button newDeck, renameDeck, removeDeck, defaultDeck, switchKey, addMapping, reuseMapping, moveMapping;
        private readonly FlowLayoutPanel toolbar = NewFlow();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Timer timer = new Timer { Interval = 15 };
        private readonly HashSet<int> swallowed = new HashSet<int>();
        private readonly Dictionary<string, int> positions = new Dictionary<string, int>();
        private readonly ToolTip tips = new ToolTip();
        private StudioKeyboard keyboard;
        private MidiOutput midi;
        private MappingEngine engine;
        private StatusOverlay overlay;
        private DataGridViewCell capturing;
        private System.Threading.CancellationTokenSource learning;
        private Profile draft;
        private MappingScope editingScope;
        private DeckConfiguration editingDeck;
        private readonly int[] sessionDecks = { -1, -1 };
        private bool loadingSelectors;
        private bool active, prepared;
        private string preferredOutput = Profile.DefaultPort;

        internal MapperWindow()
        {
            Text = "B2B Keyboard Mapper";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1120, 668);
            MinimumSize = new Size(820, 608);
            ConsoleTheme.StyleWindow(this);
            SuspendLayout();

            var layout = NewStack(5);
            layout.Padding = new Padding(22, 13, 22, 9);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 73));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

            var header = NewStack(2);
            header.AutoSize = true;
            header.ColumnCount = 2;
            header.ColumnStyles[0].SizeType = SizeType.Percent;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Margin = new Padding(0, 0, 0, 14);
            header.Controls.Add(ConsoleTheme.Label(Text, ConsoleTheme.Ink, ConsoleTheme.Title), 0, 0);
            header.Controls.Add(ConsoleTheme.Label("仅在 B2B Studio 位于前台时发送 MIDI", ConsoleTheme.Muted), 0, 1);

            var headerControls = NewStack(2);
            headerControls.AutoSize = true;
            header.Controls.Add(headerControls, 1, 0);
            header.SetRowSpan(headerControls, 2);
            var enableRow = NewFlow(false);
            enableRow.Anchor = AnchorStyles.Right;
            mappingStatus.Size = new Size(95, 37);
            mappingStatus.Margin = new Padding(0, 2, 14, 0);
            enableRow.Controls.Add(mappingStatus);
            toggle = AddButton(enableRow, "启用映射", ToggleFromWindow, true);
            toggle.Margin = new Padding(0, 2, 0, 0);
            headerControls.Controls.Add(enableRow, 0, 0);
            var shortcuts = NewFlow(false);
            shortcuts.Anchor = AnchorStyles.Right;
            shortcuts.Margin = new Padding(0, 4, 0, 0);
            var pauseKey = ConsoleTheme.Label("F8", ConsoleTheme.Accent, ConsoleTheme.Key);
            pauseKey.BackColor = ConsoleTheme.Raised;
            pauseKey.Padding = new Padding(5, 1, 5, 1);
            pauseKey.Margin = new Padding(0, 0, 7, 0);
            shortcuts.Controls.Add(pauseKey);
            shortcuts.Controls.Add(ConsoleTheme.Label("暂停／恢复", ConsoleTheme.Muted));
            headerControls.Controls.Add(shortcuts, 0, 1);
            layout.Controls.Add(header, 0, 0);

            var decks = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 11) };
            decks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            decks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            decks.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            leftStatus = new DeckCard("左组 · 当前 Deck", tips) { Margin = new Padding(0, 0, 6, 0) };
            rightStatus = new DeckCard("右组 · 当前 Deck", tips) { Margin = new Padding(6, 0, 0, 0) };
            decks.Controls.Add(leftStatus, 0, 0);
            decks.Controls.Add(rightStatus, 1, 0);
            layout.Controls.Add(decks, 0, 1);

            var connection = new ConsolePanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 11), Padding = new Padding(13, 7, 12, 7)
            };
            var connectionRow = NewStack(2);
            connectionRow.ColumnCount = 2;
            connectionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            connectionRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
            connectionRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var connectionTitle = ConsoleTheme.Label("MIDI 连接", ConsoleTheme.Ink, ConsoleTheme.Strong);
            connectionTitle.Anchor = AnchorStyles.Left;
            connectionRow.Controls.Add(connectionTitle, 0, 0);
            AddLabel(ports, "输出");
            AddInput(ports, output);
            AddButton(ports, "刷新", RefreshPorts);
            AddLabel(ports, "通道", 8);
            AddInput(ports, channel);
            AddLabel(ports, "Studio 输入（可选）", 8);
            AddInput(ports, inputName);
            connectionRow.Controls.Add(ports, 0, 1);
            connectionRow.SetColumnSpan(ports, 2);
            overlayToggle.Anchor = AnchorStyles.Right;
            overlayToggle.ForeColor = ConsoleTheme.Muted;
            overlayToggle.Margin = new Padding(8, 0, 0, 0);
            overlayToggle.CheckedChanged += delegate { UpdateOverlayVisibility(); };
            connectionRow.Controls.Add(overlayToggle, 1, 0);
            connection.Controls.Add(connectionRow);
            layout.Controls.Add(connection, 0, 2);
            tips.SetToolTip(inputName, "输入端口名称与输出一致时留空即可。此名称用于识别学习结果和导出 Studio 预设。");

            var workbench = new ConsolePanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(12, 8, 12, 10) };
            var editor = NewStack(4);
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var workbenchHeader = NewFlow();
            var editorTitle = ConsoleTheme.Label("映射编辑", ConsoleTheme.Ink, ConsoleTheme.Strong);
            editorTitle.Margin = new Padding(0, 0, 12, 0);
            workbenchHeader.Controls.Add(editorTitle);
            workbenchHeader.Controls.Add(ConsoleTheme.Label("双击键位录入按键或组合键", ConsoleTheme.Muted));
            workbenchHeader.Margin = new Padding(0, 2, 0, 6);
            editor.Controls.Add(workbenchHeader, 0, 0);

            editor.Controls.Add(deckEditor, 0, 1);
            AddLabel(deckEditor, "范围");
            scopeSelector.Items.AddRange(new object[] { "共用映射", "左组", "右组" });
            AddInput(deckEditor, scopeSelector);
            AddInput(deckEditor, deckSelector);
            newDeck = AddButton(deckEditor, "新增 Deck", AddDeck);
            renameDeck = AddButton(deckEditor, "重命名", RenameDeck);
            removeDeck = AddButton(deckEditor, "删除 Deck", DeleteDeck);
            defaultDeck = AddButton(deckEditor, "设为默认", SetDefaultDeck);
            switchKey = AddButton(deckEditor, "切换键", SetSwitchKey);
            scopeSelector.AccessibleName = "编辑范围";
            deckSelector.AccessibleName = "要编辑的 Deck";
            output.AccessibleName = "MIDI 输出";
            channel.AccessibleName = "MIDI 通道";
            inputName.AccessibleName = "Studio 输入名";
            scopeSelector.SelectedIndexChanged += delegate { ChangeEditor(true); };
            deckSelector.SelectedIndexChanged += delegate { ChangeEditor(false); };

            toolbar.Margin = new Padding(0, 5, 0, 7);
            editor.Controls.Add(toolbar, 0, 2);
            var editTools = NewFlow(false);
            editTools.Margin = new Padding(0, 0, 14, 0);
            toolbar.Controls.Add(editTools);
            addMapping = AddButton(editTools, "新增映射", AddRow);
            AddButton(editTools, "删除所选", DeleteRow);
            moveMapping = AddButton(editTools, "移动所选", MoveRow);
            reuseMapping = AddButton(editTools, "复用映射", ReuseRow);
            AddButton(editTools, "绑定选中控件", Learn);

            var fileTools = NewFlow(false);
            toolbar.Controls.Add(fileTools);
            AddButton(fileTools, "保存键位", Save);
            AddButton(fileTools, "导入键位", ImportKeys);
            AddButton(fileTools, "导出键位", ExportKeys);
            AddButton(fileTools, "导出 Studio 预设", ExportStudio);

            SetupGrid();
            editor.Controls.Add(grid, 0, 3);
            workbench.Controls.Add(editor);
            layout.Controls.Add(workbench, 0, 3);
            layout.Controls.Add(status, 0, 4);
            Controls.Add(layout);
            ResumeLayout(true);

            output.SelectionChangeCommitted += delegate
            {
                var port = output.SelectedItem as MidiOutput.Port;
                if (port != null) preferredOutput = port.Name;
            };
            timer.Tick += delegate { Tick(); };
            Shown += delegate
            {
                OpenKeyboard();
                UpdateOverlayVisibility();
            };
            FormClosing += delegate
            {
                if (learning != null) learning.Cancel();
                timer.Stop();
                Pause();
                if (keyboard != null) keyboard.Dispose();
                if (overlay != null) overlay.Dispose();
                tips.Dispose();
            };
            LoadProfile();
        }

        private static TableLayoutPanel NewStack(int rows)
        {
            var stack = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = rows };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return stack;
        }

        private static FlowLayoutPanel NewFlow(bool wrap = true)
        {
            return new FlowLayoutPanel
            {
                Dock = wrap ? DockStyle.Fill : DockStyle.None, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, WrapContents = wrap
            };
        }

        private static void AddLabel(Control parent, string text, int left = 0)
        {
            var label = ConsoleTheme.Label(text, ConsoleTheme.Muted);
            label.Margin = new Padding(left, 9, 6, 0);
            parent.Controls.Add(label);
        }

        private static void AddInput(Control parent, Control input)
        {
            ConsoleTheme.StyleInput(input);
            input.Margin = new Padding(0, 5, 8, 2);
            parent.Controls.Add(input);
        }

        private static Button AddButton(Control parent, string text, Action click, bool primary = false)
        {
            var button = new ConsoleButton(text, primary);
            button.Click += delegate { click(); };
            parent.Controls.Add(button);
            return button;
        }

        private void SetupGrid()
        {
            grid.Dock = DockStyle.Fill;
            grid.Margin = Padding.Empty;
            grid.BackgroundColor = ConsoleTheme.Field;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.GridColor = ConsoleTheme.Line;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = ConsoleTheme.Raised;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = ConsoleTheme.Ink;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = ConsoleTheme.Raised;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = ConsoleTheme.Ink;
            grid.ColumnHeadersDefaultCellStyle.Font = ConsoleTheme.Strong;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(5, 0, 0, 0);
            grid.ColumnHeadersHeight = 36;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.DefaultCellStyle.BackColor = ConsoleTheme.Surface;
            grid.DefaultCellStyle.ForeColor = ConsoleTheme.Ink;
            grid.DefaultCellStyle.SelectionBackColor = ConsoleTheme.AccentSurface;
            grid.DefaultCellStyle.SelectionForeColor = ConsoleTheme.Ink;
            grid.DefaultCellStyle.Padding = new Padding(5, 0, 5, 0);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(28, 34, 38);
            grid.RowTemplate.Height = 34;
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.MultiSelect = false;
            grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

            grid.AccessibleName = "按键映射表";

            AddColumn("名称", 175);
            var mode = new DataGridViewComboBoxColumn
            {
                HeaderText = "类型", Width = 104, FlatStyle = FlatStyle.Flat,
                DisplayStyleForCurrentCellOnly = true
            };
            mode.Items.AddRange(Modes);
            grid.Columns.Add(mode);
            AddColumn("按下 / 减少", 122, true);
            AddColumn("增加", 122, true);
            AddColumn("Note / CC", 82);
            AddColumn("步进", 62);
            AddColumn("初始值", 76);
            var targets = AddColumn("Studio 控件 ID（可选）", 195);
            targets.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            targets.MinimumWidth = 195;
            targets.ToolTipText = "学习成功后自动填入并保存，用于导出 Studio 预设。多个 ID 以逗号分隔。";
            foreach (DataGridViewColumn column in grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.CellPainting += PaintChord;
            grid.EditingControlShowing += delegate(object sender, DataGridViewEditingControlShowingEventArgs args)
            {
                args.Control.BackColor = ConsoleTheme.Field;
                args.Control.ForeColor = ConsoleTheme.Ink;
            };
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

        private void PaintChord(object sender, DataGridViewCellPaintingEventArgs args)
        {
            if (args.RowIndex < 0 || (args.ColumnIndex != 2 && args.ColumnIndex != 3)) return;
            bool recording = capturing != null && args.RowIndex == capturing.RowIndex && args.ColumnIndex == capturing.ColumnIndex;
            string text = recording ? "录入中…" : Convert.ToString(args.FormattedValue);
            if (text.Length == 0 || text == "—") return;
            args.Paint(args.ClipBounds, args.PaintParts & ~(DataGridViewPaintParts.ContentForeground | DataGridViewPaintParts.Focus));
            var cell = args.CellBounds;
            int height = cell.Height - 10;
            var font = recording ? ConsoleTheme.Body : ConsoleTheme.Key;
            int width = Math.Min(cell.Width - 14, Math.Max(26, TextRenderer.MeasureText(text, font).Width + 9));
            ConsoleTheme.PaintKey(args.Graphics, new Rectangle(cell.Left + 7, cell.Top + 4, width, height),
                text, font, ConsoleTheme.Field, recording ? ConsoleTheme.Accent : ConsoleTheme.Line,
                !grid.Enabled ? ConsoleTheme.Dim : recording ? ConsoleTheme.Accent : ConsoleTheme.Ink);
            args.Paint(args.ClipBounds, args.PaintParts & DataGridViewPaintParts.Focus);
            args.Handled = true;
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
                if (output.Items.Count > 0 && output.SelectedIndex >= 0)
                    SetStatus("已暂停，点击「启用映射」开始。");
            }
            catch (Exception error)
            {
                Fill(Profile.Defaults());
                SetStatus("配置读取失败，已载入示例。 " + error.Message, true);
            }
        }

        private void Fill(Profile profile)
        {
            profile.Upgrade();
            draft = profile;
            preferredOutput = String.IsNullOrWhiteSpace(profile.Output) ? Profile.DefaultPort : profile.Output;
            inputName.Text = profile.Input ?? "";
            channel.Value = profile.Channel;
            sessionDecks[0] = profile.Left.Decks.Count == 0 ? -1 : profile.Left.DefaultDeck;
            sessionDecks[1] = profile.Right.Decks.Count == 0 ? -1 : profile.Right.DefaultDeck;
            var scope = sessionDecks[0] >= 0 ? MappingScope.Left
                : sessionDecks[1] >= 0 ? MappingScope.Right : MappingScope.Common;
            ShowEditor(scope, sessionDecks[0] >= 0 ? sessionDecks[0] : sessionDecks[1]);
            UpdateDeckStatus();
            RefreshPorts();
        }

        private List<Binding> EditedBindings
        {
            get { return editingScope == MappingScope.Common ? draft.Bindings : editingDeck == null ? null : editingDeck.Bindings; }
        }

        private void ChangeEditor(bool scopeChanged)
        {
            if (loadingSelectors) return;
            try
            {
                CommitBindings();
                var scope = (MappingScope)(scopeSelector.SelectedIndex - 1);
                int index = scope == MappingScope.Common ? -1
                    : scopeChanged ? sessionDecks[(int)scope] : deckSelector.SelectedIndex;
                ShowEditor(scope, index);
            }
            catch (InvalidDataException error)
            {
                RefreshSelectors();
                SetStatus(error.Message, true);
            }
        }

        private void ShowEditor(MappingScope scope, int index)
        {
            editingScope = scope;
            editingDeck = scope == MappingScope.Common || index < 0 ? null : draft.Group(scope).Decks[index];
            RefreshSelectors();
            grid.Rows.Clear();
            var bindings = EditedBindings;
            grid.EmptyMessage = bindings == null ? "此组还没有 Deck。请先新增 Deck。" : "此范围暂无映射。可以新增或复用映射。";
            if (bindings != null)
                foreach (var binding in bindings) FillRow(binding);
        }

        private void RefreshSelectors()
        {
            loadingSelectors = true;
            try
            {
                scopeSelector.SelectedIndex = (int)editingScope + 1;
                var group = editingScope == MappingScope.Common ? null : draft.Group(editingScope);
                deckSelector.Items.Clear();
                if (group != null)
                {
                    for (int index = 0; index < group.Decks.Count; index++)
                        deckSelector.Items.Add(group.Decks[index].Name + (index == group.DefaultDeck ? "（默认）" : ""));
                    deckSelector.SelectedIndex = group.Decks.IndexOf(editingDeck);
                }
                deckSelector.Enabled = renameDeck.Enabled = removeDeck.Enabled = defaultDeck.Enabled = editingDeck != null;
                tips.SetToolTip(deckSelector, editingDeck == null ? "选择要编辑的 Deck。" : editingDeck.Name);
                newDeck.Enabled = switchKey.Enabled = group != null;
                string shortcut = group == null || group.SwitchKey == 0 ? "未设置切换键" : "切换键 " + Chord.Display(group.SwitchKey);
                tips.SetToolTip(switchKey, shortcut);
                switchKey.AccessibleDescription = shortcut;
                addMapping.Enabled = moveMapping.Enabled = reuseMapping.Enabled = EditedBindings != null;
            }
            finally { loadingSelectors = false; }
        }

        private void UpdateDeckStatus()
        {
            UpdateDeckCard(leftStatus, MappingScope.Left);
            UpdateDeckCard(rightStatus, MappingScope.Right);
            UpdateOverlayState();
        }

        private void UpdateDeckCard(DeckCard card, MappingScope scope)
        {
            var group = draft.Group(scope);
            string shortcut = group.Decks.Count > 1 && group.SwitchKey != 0 ? Chord.Display(group.SwitchKey) : "";
            card.SetDeck(CurrentDeckName(scope), shortcut);
        }

        private string CurrentDeckName(MappingScope scope)
        {
            int index = sessionDecks[(int)scope];
            return index < 0 ? "未配置 Deck" : draft.Group(scope).Decks[index].Name;
        }

        private void UpdateOverlayVisibility()
        {
            if (!overlayToggle.Checked)
            {
                if (overlay != null) overlay.Hide();
                return;
            }
            if (overlay == null)
            {
                var positionPath = Path.Combine(Path.GetDirectoryName(Profile.FilePath), "overlay.json");
                overlay = new StatusOverlay(positionPath, () => Screen.FromControl(this).WorkingArea);
            }
            UpdateOverlayState();
            overlay.Show();
        }

        private void UpdateOverlayState()
        {
            if (overlay != null)
                overlay.SetState(active, CurrentDeckName(MappingScope.Left), CurrentDeckName(MappingScope.Right));
        }

        private void AddDeck()
        {
            ShowErrors(delegate
            {
                CommitBindings();
                var group = draft.Group(editingScope);
                int number = group.Decks.Count + 1;
                while (group.Decks.Any(deck => String.Equals(deck.Name, "Deck " + number, StringComparison.OrdinalIgnoreCase)))
                    number++;
                string name = EditDeckName(group, "新增 Deck", "Deck " + number, null);
                if (name == null) return;

                group.Decks.Add(new DeckConfiguration { Name = name, Bindings = new List<Binding>() });
                if (group.Decks.Count == 1) sessionDecks[(int)editingScope] = 0;
                ShowEditor(editingScope, group.Decks.Count - 1);
                UpdateDeckStatus();
                SetStatus("已新增「" + name + "」，可添加或复用映射。");
            });
        }

        private void RenameDeck()
        {
            ShowErrors(delegate
            {
                var group = draft.Group(editingScope);
                string name = EditDeckName(group, "重命名 Deck", editingDeck.Name, editingDeck);
                if (name == null) return;
                editingDeck.Name = name;
                RefreshSelectors();
                UpdateDeckStatus();
            });
        }

        private string EditDeckName(DeckGroup group, string title, string initial, DeckConfiguration current)
        {
            var textBox = new TextBox { Text = initial, MaxLength = 100 };
            string name = ShowEditorDialog(title, "Deck 配置名称", textBox, () => textBox.Text.Trim());
            if (name == null) return null;
            if (name.Length == 0 || group.Decks.Any(deck => deck != current
                && String.Equals(deck.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("请输入该组内唯一的 Deck 名称。");
            return name;
        }

        private void DeleteDeck()
        {
            ShowErrors(delegate
            {
                CommitBindings();
                var group = draft.Group(editingScope);
                string name = editingDeck.Name;
                if (MessageBox.Show(this, "删除「" + name + "」及其中的映射？", "删除 Deck",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
                    return;

                int removed = group.Decks.IndexOf(editingDeck);
                group.Decks.RemoveAt(removed);
                if (group.DefaultDeck == removed) group.DefaultDeck = 0;
                else if (group.DefaultDeck > removed) group.DefaultDeck--;
                int side = (int)editingScope;
                if (group.Decks.Count == 0) sessionDecks[side] = -1;
                else if (sessionDecks[side] == removed) sessionDecks[side] = group.DefaultDeck;
                else if (sessionDecks[side] > removed) sessionDecks[side]--;
                ShowEditor(editingScope, sessionDecks[side]);
                UpdateDeckStatus();
                SetStatus("已删除「" + name + "」。");
            });
        }

        private void SetDefaultDeck()
        {
            var group = draft.Group(editingScope);
            group.DefaultDeck = group.Decks.IndexOf(editingDeck);
            RefreshSelectors();
            SetStatus("下次载入时将启用「" + editingDeck.Name + "」。");
        }

        private void SetSwitchKey()
        {
            using (var dialog = new ShortcutDialog(Profile.GroupName(editingScope)))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                draft.Group(editingScope).SwitchKey = dialog.Shortcut;
                RefreshSelectors();
                UpdateDeckStatus();
                SetStatus(dialog.Shortcut == 0 ? "已清除切换键。" : "已设置切换键 " + Chord.Display(dialog.Shortcut) + "。");
            }
        }

        private sealed class BindingLocation
        {
            internal MappingScope Scope;
            internal int Index;
            internal string Name;
            internal List<Binding> Bindings;
            public override string ToString() { return Name; }
        }

        private IEnumerable<BindingLocation> BindingLocations()
        {
            yield return new BindingLocation { Scope = MappingScope.Common, Index = -1, Name = "共用映射", Bindings = draft.Bindings };
            foreach (var scope in new[] { MappingScope.Left, MappingScope.Right })
            {
                var group = draft.Group(scope);
                for (int index = 0; index < group.Decks.Count; index++)
                {
                    var deck = group.Decks[index];
                    yield return new BindingLocation { Scope = scope, Index = index, Name = Profile.GroupName(scope) + " / " + deck.Name,
                        Bindings = deck.Bindings };
                }
            }
        }

        private void MoveRow()
        {
            if (grid.CurrentRow == null) return;
            ShowErrors(delegate
            {
                int index = grid.CurrentRow.Index;
                CommitBindings();
                var source = EditedBindings;
                var destinations = BindingLocations().Where(location => location.Bindings != source).ToArray();
                if (destinations.Length == 0) throw new InvalidDataException("请先在左组或右组新增一个 Deck。");
                var choices = NewChoices(destinations);
                var destination = ShowEditorDialog("移动映射", "选择要移到的配置", choices, () => (BindingLocation)choices.SelectedItem);
                if (destination == null) return;
                destination.Bindings.Add(source[index]);
                source.RemoveAt(index);
                ShowEditor(destination.Scope, destination.Index);
                grid.CurrentCell = grid.Rows[grid.Rows.Count - 1].Cells[0];
                SetStatus("映射已移至「" + destination.Name + "」。");
            });
        }

        private sealed class ReuseChoice
        {
            internal Binding Binding;
            internal string Name;
            public override string ToString() { return Name; }
        }

        private void ReuseRow()
        {
            ShowErrors(delegate
            {
                CommitBindings();
                var sources = BindingLocations().SelectMany(location => location.Bindings
                    .Where(binding => binding.TargetIds().Any())
                    .Select(binding => new ReuseChoice { Binding = binding, Name = location.Name + " / " + binding.Name })).ToArray();
                if (sources.Length == 0) throw new InvalidDataException("请先在一条映射中填写或学习 Studio 控件 ID。");
                var choices = NewChoices(sources);
                var source = ShowEditorDialog("复用映射", "选择要复用的 Studio 绑定", choices,
                    () => ((ReuseChoice)choices.SelectedItem).Binding);
                if (source == null) return;
                FillRow(new Binding { Name = source.Name, Mode = source.Mode, Number = source.Number,
                    Step = source.Step, Initial = source.Initial, Targets = source.Targets });
                CaptureChord(grid.Rows[grid.Rows.Count - 1].Cells[2]);
                grid.Focus();
            });
        }

        private static ComboBox NewChoices(object[] items)
        {
            var choices = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            choices.Items.AddRange(items);
            choices.SelectedIndex = 0;
            return choices;
        }

        private T ShowEditorDialog<T>(string title, string label, Control editor, Func<T> result) where T : class
        {
            using (var dialog = new Form
            {
                Text = title, ClientSize = new Size(470, 139), StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                ShowInTaskbar = false
            })
            {
                ConsoleTheme.StyleWindow(dialog);
                var prompt = ConsoleTheme.Label(label, ConsoleTheme.Ink, ConsoleTheme.Strong);
                prompt.Location = new Point(18, 16);
                dialog.Controls.Add(prompt);
                editor.SetBounds(18, 45, 434, 25);
                ConsoleTheme.StyleInput(editor);
                editor.AccessibleName = label;
                dialog.Controls.Add(editor);
                var accept = new ConsoleButton("确定", true) { DialogResult = DialogResult.OK, AutoSize = false, Bounds = new Rectangle(246, 85, 110, 37) };
                var cancel = new ConsoleButton("取消") { DialogResult = DialogResult.Cancel, AutoSize = false, Bounds = new Rectangle(364, 85, 88, 37) };
                dialog.Controls.Add(accept);
                dialog.Controls.Add(cancel);
                dialog.AcceptButton = accept;
                dialog.CancelButton = cancel;
                dialog.ActiveControl = editor;
                return dialog.ShowDialog(this) == DialogResult.OK ? result() : null;
            }
        }

        private void FillRow(Binding binding)
        {
            int mode = Array.IndexOf(ModeIds, binding.Mode);
            var index = grid.Rows.Add(binding.Name, Modes[mode], null, null,
                binding.Number, binding.Step, binding.Initial, binding.Targets);
            var row = grid.Rows[index];
            SetChord(row.Cells[2], binding.Down);
            SetChord(row.Cells[3], binding.Up);
            UpdateRow(row);
        }

        private static void SetChord(DataGridViewCell cell, int value)
        {
            string text = Chord.Display(value);
            cell.Tag = value;
            cell.Value = text;
            cell.ToolTipText = value == 0 ? "" : text;
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
                row.Cells[index].Style.ForeColor = available ? ConsoleTheme.Ink : ConsoleTheme.Dim;
            }
        }

        private void AddRow()
        {
            ShowErrors(delegate
            {
                CommitBindings();
                var used = new HashSet<int>(draft.AllBindings().Where(binding => binding.IsNote).Select(binding => binding.Number));
                int number = Enumerable.Range(0, 128).FirstOrDefault(candidate => !used.Contains(candidate));
                if (used.Contains(number)) throw new InvalidDataException("Note 编号已用完。");
                FillRow(new Binding { Name = "新映射", Mode = "note", Number = number, Step = 2, Initial = 64 });
                grid.CurrentCell = grid.Rows[grid.Rows.Count - 1].Cells[0];
            });
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
            grid.InvalidateCell(cell);
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
            SetChord(capturing, value);
            EndCapture();
            SetStatus(value == 0 ? "键位已清除。" : "已录入 " + Chord.Display(value) + "。");
            return true;
        }

        private void EndCapture()
        {
            if (capturing == null) return;
            var cell = capturing;
            capturing = null;
            if (cell.DataGridView == grid) grid.InvalidateCell(cell);
            SetStatus("已退出键位录入。");
        }

        private void CommitBindings()
        {
            var edited = EditedBindings;
            if (edited == null) return;
            if (!grid.EndEdit()) throw new InvalidDataException("请完成当前单元格的编辑。");
            EndCapture();
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
            edited.Clear();
            edited.AddRange(bindings);
        }

        private Profile ReadGrid()
        {
            CommitBindings();
            draft.Output = preferredOutput;
            draft.Input = inputName.Text.Trim();
            draft.Channel = (int)channel.Value;
            draft.Validate();
            return draft;
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
            if (output.Items.Count == 0) SetStatus("找不到 MIDI 输出。请先创建 B2B Keys 虚拟端口，再点刷新。", true);
            else if (output.SelectedIndex < 0) SetStatus("请选择 MIDI 输出端口。", true);
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
                var binding = EditedBindings[row.Index];
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

                profile.SaveLearnedTarget(binding, target, Profile.FilePath);
                for (int index = 0; index < EditedBindings.Count; index++)
                    grid.Rows[index].Cells[7].Value = EditedBindings[index].Targets;
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
            if (!profile.AllBindings().Any()) throw new InvalidOperationException("请先添加至少一条映射。");
            midi = OpenOutput(profile.Output);
            engine = new MappingEngine(profile, midi.Send, positions);
            engine.SelectDeck(MappingScope.Left, sessionDecks[0]);
            engine.SelectDeck(MappingScope.Right, sessionDecks[1]);
            active = true;
            SetEditorEnabled(false);
            SetStatus("映射已启用 · 切回 B2B Studio 即可使用。");
        }

        private void SetEditorEnabled(bool enabled)
        {
            grid.Enabled = ports.Enabled = deckEditor.Enabled = toolbar.Enabled = enabled;
            toggle.Text = active ? "暂停映射" : "启用映射";
            mappingStatus.SetMappingActive(active);
            UpdateOverlayState();
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
                if (!active) return false;
                MappingScope scope;
                if (engine.TrySwitchDeck(key | modifiers, out scope))
                {
                    sessionDecks[(int)scope] = engine.CurrentDeck(scope);
                    UpdateDeckStatus();
                }
                else if (!engine.Press(key, modifiers, clock.ElapsedMilliseconds)) return false;
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
            status.ForeColor = warning ? ConsoleTheme.Alert : active ? ConsoleTheme.Accent : ConsoleTheme.Muted;
            status.Text = text;
            tips.SetToolTip(status, text);
        }
    }

    internal sealed class ShortcutDialog : Form
    {
        internal int Shortcut { get; private set; }

        internal ShortcutDialog(string group)
        {
            Text = group + "切换键";
            ClientSize = new Size(385, 114);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = ShowInTaskbar = false;
            ConsoleTheme.StyleWindow(this);
            var prompt = ConsoleTheme.Label("按下新按键或组合键。Delete 清除，Esc 取消。", ConsoleTheme.Ink);
            prompt.Location = new Point(18, 19);
            Controls.Add(prompt);
            var cancel = new ConsoleButton("取消") { DialogResult = DialogResult.Cancel, AutoSize = false, Bounds = new Rectangle(279, 60, 88, 37) };
            Controls.Add(cancel);
            CancelButton = cancel;
        }

        protected override bool ProcessCmdKey(ref Message message, Keys key)
        {
            if (key == Keys.Escape) return base.ProcessCmdKey(ref message, key);
            if (key == Keys.Delete || Chord.CanBind((int)key))
            {
                Shortcut = key == Keys.Delete ? 0 : (int)key;
                DialogResult = DialogResult.OK;
            }
            return true;
        }
    }

    internal sealed class MappingGrid : DataGridView
    {
        internal Func<Keys, bool> HandleKeys;
        private string emptyMessage;

        internal MappingGrid() { DoubleBuffered = true; }

        internal string EmptyMessage
        {
            set { emptyMessage = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            if (Rows.Count != 0 || String.IsNullOrEmpty(emptyMessage)) return;
            var bounds = new Rectangle(14, ColumnHeadersHeight + 12, Math.Max(0, ClientSize.Width - 28),
                Math.Max(0, ClientSize.Height - ColumnHeadersHeight - 34));
            TextRenderer.DrawText(args.Graphics, emptyMessage, ConsoleTheme.Body, bounds, ConsoleTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys key)
        {
            return (HandleKeys != null && HandleKeys(key)) || base.ProcessCmdKey(ref message, key);
        }
    }
}
