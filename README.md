# InterviewTrace

一个用于个人面试复盘的 Windows 桌面工具。它分别录制麦克风和所选耳机或扬声器的声音，按需使用云端 API 转写，再按时间和声音来源整理为文字。

## 下载和更新

在 [GitHub Releases](https://github.com/SnoozeOneClass/InterviewTrace/releases) 下载 Windows x64 压缩包，解压整个文件夹后运行 `InterviewTrace.exe`，无需额外安装 .NET。更新时可将新版本解压到新文件夹，继续使用原来的录音和记录索引位置。

当前为试用版，首次在面试电脑上使用前，请先试录并分别回听两路声音。真实会议设备和云端账号的兼容性仍需实测。

## 使用

1. 启动 `InterviewTrace.exe`。在“设置”中选择默认保存目录；在录音页也可以为本次录音单独选择目录。
2. 分别选择面试软件实际使用的麦克风与耳机 / 扬声器。可先点击“试录 5 秒”，再分别回听两路声音。试录文件是临时文件，关闭程序时会尝试清理。
3. 按需填写公司、岗位与面试轮次，点击“开始录音”；面试结束后点击“停止并保存”。
4. 在“面试记录”中查看历史记录、修改信息、回听录音或打开保存文件夹。
5. 在“设置”中自行填写阿里云百炼 API Key 并保存，然后在记录页手动点击“开始 / 继续转写”。文字生成后可复制或另存为文本文件。不要将密钥发到聊天中。

麦克风音频和系统音频分别保存为 `microphone.wav` 和 `system.wav`，转写结果保存为 `transcript.txt`。每次面试使用独立文件夹；改变默认保存目录不会移动旧文件。记录索引是 SQLite 文件，可以在设置中查看或选择其位置。

转写使用百炼的 `fun-asr-flash-2026-06-15` 接口，将两路音频切分后直接上传；调用依服务商规则计费，分段重叠也会增加少量处理时长。API Key 使用 Windows 当前用户的数据保护机制保存在本机，不写入源码。实际账号需要开通相应模型；接口地址、模型名称及计费以服务商为准。[接口文档](https://help.aliyun.com/zh/model-studio/fun-asr-flash-recorded-speech-recognition-http-api)

网络中断后，已完成的转写片段会保留。如果某个请求已经发出但未收到确定结果，继续时需要确认可能重复计费。设备断开、意外退出或磁盘故障时应回听已保存的录音；能恢复多少数据取决于实际中断情况。

## 从源码构建

需要 Windows 和 .NET 10 SDK。在项目目录执行：

```powershell
dotnet restore InterviewTrace.slnx
dotnet build InterviewTrace.slnx -c Release
dotnet run --project tests/InterviewTrace.Smoke -c Release
dotnet publish src/InterviewTrace.App -c Release -r win-x64 --self-contained true -o artifacts/InterviewTrace-win-x64
```

如果使用项目内下载的 SDK，`dotnet` 可替换为 `.local\dotnet\dotnet.exe`。发布目录中的 `InterviewTrace.exe` 与同目录依赖需要一起保留；根据目标电脑架构选择对应的运行时。

## 当前验证范围

已使用模拟音频和模拟云端响应验证双路时间线、记录索引、密钥本地保护及转写续传逻辑；真实耳机、麦克风、长时间面试录音和实际百炼账号仍需在面试电脑上试用验证。
