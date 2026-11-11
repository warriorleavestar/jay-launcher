# Jay Android 实验版

Android 版是一个独立的 Avalonia 移动端工程，不会把桌面端的窗口、进程启动和 EasyTier 代码强行带到手机上。

应用显示版本和整数构建号分别跟随 `src/Directory.Build.props` 的 `Version`、`MobileApplicationVersion`，不再需要在 Android 工程和界面中重复修改版本号。

## 现在能做什么

- 在应用私有目录生成并加密保存设备身份。
- 解析 Jay 服务器的一次性邀请码，登记手机并加密保存访问密钥。
- 给已保存的 Jay 服务器开一个 `127.0.0.1:<端口>` 网关。其他 Android Minecraft Java 启动器如果能访问同一手机的回环端口，可以把这个地址当服务器地址手动连接。
- 网关连接期间启动 Android 前台服务，切到游戏后仍尽量保持回环端口可用。
- 一键复制网关地址并打开 [Fold Craft Launcher (FCL)](https://github.com/FCL-Team/FoldCraftLauncher)。在 FCL 的 Minecraft 多人游戏页添加这个地址，游戏内的虚拟摇杆、按键和鼠标操作由 FCL 提供。
- 跟随系统语言，提供简中、繁中、英文和日文。

## 现在不能做什么

- Jay 不内置 Minecraft Java 运行时。FCL 需要另行安装，并由 FCL 负责 JVM/LWJGL、图形桥、微软账号登录和触屏控制。FCL 使用 GPL-3.0，Jay 只通过 Android 启动 Intent 与其协作，不包含或复制 FCL 源码/二进制。
- 不能创建普通 Jay 房间。EasyTier 2.6.4 没有在本项目中集成 Android 可执行文件。
- PR 的 CI 产物使用测试签名；`android-preview` 发布包使用固定的预览版签名，可覆盖升级并保留数据，但尚未上架 Google Play。

## 本地构建

需要 .NET 10 Android workload、Android SDK 和 JDK 21。可按 Microsoft 提供的 `InstallAndroidDependencies` 目标安装与当前项目匹配的依赖：

```sh
dotnet workload install android
dotnet build src/Amber.Android/Amber.Android.csproj \
  -t:InstallAndroidDependencies -f net10.0-android \
  -p:AndroidSdkDirectory=/absolute/path/android-sdk \
  -p:JavaSdkDirectory=/absolute/path/jdk \
  -p:AcceptAndroidSdkLicenses=true

dotnet publish src/Amber.Android/Amber.Android.csproj -c Release \
  -p:AndroidSdkDirectory=/absolute/path/android-sdk \
  -p:JavaSdkDirectory=/absolute/path/jdk \
  -p:AndroidPackageFormats=apk
```

产物在 `src/Amber.Android/bin/Release/net10.0-android/publish/`。对外发布前要按 [Avalonia Android 发布文档](https://docs.avaloniaui.net/docs/deployment/android) 配置独立密钥库，密码只能放在 GitHub Secrets 中。

CI 会用 Android Build Tools 的 `zipalign -P 16` 检查 APK，并逐个检查 ARM64 `.so` 的 ELF `LOAD` 段是否至少按 16 KiB 对齐。预览发布使用仓库 Secrets 中的固定签名，后续覆盖安装不会清除玩家的语言、设备身份和已保存服务器。

## 下一步

1. 在 Android 真机上用 FCL 完成从 Jay 网关到进服的端到端验证。
2. 为前台服务增加通知权限引导和明确的停止操作。
3. 如果 FCL 以后提供官方 deep link，传入服务器地址，去掉手动粘贴步骤。
