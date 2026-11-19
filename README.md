# B2B Keyboard Mapper

Windows 键盘转 MIDI 工具，适用于 B2B Studio 桌面版。工具通过 Studio 的 MIDI 映射控制控件，可以从界面配置按键、组合键、旋钮步进和 MIDI 通道。

## 第一次使用

1. 启动 loopMIDI，创建一个名为 `B2B Keys` 的端口，并让 loopMIDI 保持运行。如果已有 WinMM 可见的 MIDI 回环端口，也可选择那个端口。[loopMIDI 官网](https://www.tobias-erichsen.de/software/loopmidi.html)
2. 打开 `B2B Keyboard Mapper.exe`，点「刷新」并选中 `B2B Keys`。
3. 将 `B2B_Keys.b2bmidi.json` 复制到「下载」或「文档」。进入 B2B Studio 的练习台或 DJ 房间，打开 **MIDI** 旁的 **▾**，选择 **Import**，选中复制后的文件。Studio 的文件选择器会限制安装目录内的文件。此预设使用 `B2B Keys` 端口和 MIDI 通道 16。如果你用了自己的端口或通道，请在工具中选好端口，再点「导出 Studio 预设」保存到「文档」。如果输入输出端口名称不同，在工具里填上 Studio 显示的输入端口名再导出。
4. 在工具里点击「启用映射」，切回 B2B Studio。切换到其他应用会释放正在按住的映射。留在 Studio 内打字时，先按 **F8** 暂停，打完再按 **F8** 恢复。

## 示例键位

| 控件 | 默认键 |
| --- | --- |
| Deck 1 PLAY / CUE | Q / W |
| Deck 2 PLAY / CUE | O / P |
| 混音器 CH 2 低频 | A 减少 / S 增加 |
| 混音器 CH 3 低频 | K 减少 / L 增加 |
| 混音器 CH 2 音量 | Z 减少 / X 增加 |
| 混音器 CH 3 音量 | N 减少 / M 增加 |
| BJM-A10 交叉推子 | 逗号向左 / 句号向右 |

示例预设涵盖 P2P-3000X 播放器及 BJM-A10、BJM-V11 混音器。换用其他面板时，可在 Studio 中重新学习对应控件。

## 自定义

- **换键**　暂停工具，双击对应键位单元格，再按键或按组合键。点击「保存键位」或「启用映射」后，本机自动保存配置。可以用「导入键位」和「导出键位」备份。只改键位和步进时，Studio 可以继续使用原 MIDI 预设。
- **增加控件**　添加一行，填上名称、类型、键位和未使用的 Note / CC 编号。在 Studio 点击 **MIDI** 开启学习，点一下目标控件。回到工具选中该行，点「发送学习信号」。Studio 显示绑定成功后，再按一次 **MIDI** 退出学习。部分 Studio 场景可能要求登录才能导入预设，可在界面提供学习时逐项绑定。
- **按钮**　按下发送 Note On，松开时发送 Note Off。适合 PLAY、CUE 等控件。
- **相对旋钮**　按键从控件当前位置增减，长按连续调节。学习后可在 Studio 的 **WHEEL TUNING** 确认类型为 **REL 2**。要用相对步进控制推子，请填写 Studio 控件 ID 并导出预设。
- **绝对 CC**　使用工具记录的 0–127 数值，适合通过 MIDI 学习绑定绝对位置的推子。每次启动工具后从「初始值」开始，首次操作可能把推子带到该位置附近。

Studio 控件 ID 可从 Studio 导出的预设中查看。可在一行填多个 ID，用逗号分隔，例如同一按键控制不同混音器面板上的对应旋钮。

配置保存在 `%LOCALAPPDATA%\B2B Keyboard Mapper\keys.json`。
