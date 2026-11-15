<p align="center"><img src="assets/icons/cookie_pixel.png" width="96" alt="翻译器"></p>

# 饼饼的开刃作

Windows 桌面中英互译小工具。按 `Ctrl + 空格` 唤出小窗，输入中文或英文，立刻给出释义、音标、近音汉字、可直接复制的回复话术和缩写版，方便在聊天窗口里快速回消息。

- 作者：Solivine　·　平台：Windows 10 / 11（自带 .NET Framework 4.x，不用另装运行库）　·　界面：C# WinForms 源码，无第三方库

## 下载就能用

到 [Releases](https://github.com/xiaobinggan94-debug/kairenzuo/releases/latest) 下载 `translator-1.0.0.exe`（程序窗口里显示的软件名是「翻译器」），双击即开。**Key 要自己弄一个**：去 [platform.deepseek.com](https://platform.deepseek.com) 注册，充点钱（按用量计费，充十几块能用挺久），在 API keys 页面新建一个，复制粘进程序首次运行的小窗就行。Key 只存你自己电脑上，不外传。

不一定非得 DeepSeek：凡是接口兼容 OpenAI 的服务商都能用，Kimi（moonshot）、通义（dashscope 兼容模式）、智谱、硅基流动、OpenRouter 这些填进来就能跑。填 Key 那个窗里除了 Key，还有「地址」和「模型名」两格，照对方文档填，填完先点「测一下」，通了再保存；两格都留空就是 DeepSeek 官方那套。

- 用法细节看 [使用说明.md](使用说明.md)；想自己编译看 [docs/BUILD.md](docs/BUILD.md)，一条命令的事
- 被杀软拦了怎么办：exe 没有代码签名，Defender / SmartScreen 误判这种独立小程序很常见，不是中毒。处理：Windows 安全中心 → 保护历史记录 → 找到那条 → 还原，并「允许在设备上」；或把文件哈希和 Release 里公布的值对一下；最彻底的是自己用 `tools\build.ps1` 编一遍，源码全在仓库里

## 许可

[PolyForm Noncommercial License 1.0.0](LICENSE)（SPDX 标识：`PolyForm-Noncommercial-1.0.0`）

**源码开放，但不是 OSI 定义的那种「开源」**——这一版许可禁止商业用途，所以严格说是 source-available。说人话就是：

| 你想干什么 | 行不行 |
|---|---|
| 自己用、拆开改、拷给朋友 | 可以，不用问 |
| 发到博客 / 论坛 / 群里，注明出处 | 可以 |
| 拿去卖、做成付费服务、塞进商用产品、在公司里当生产工具跑 | 不行 |
| 就想要商用授权 | 开个 Issue 找作者谈 |

仓库页面右上角可能把这个许可显示成 `Other` / `NOASSERTION`，那是 GitHub 的自动识别库不认识 PolyForm，不是你没写许可——以本文件和 [LICENSE](LICENSE) 全文为准。
