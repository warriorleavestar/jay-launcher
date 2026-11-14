# Jay Android 实验版

Android 版是一个独立的 Avalonia 移动端工程，不会把桌面端的窗口、进程启动和 EasyTier 代码强行带到手机上。

应用显示版本和整数构建号分别跟随 `src/Directory.Build.props` 的 `Version`、`MobileApplicationVersion`，不再需要在 Android 工程和界面中重复修改版本号。

## 现在能做什么

- 在应用私有目录生成并加密保存设备身份。
- 解析 Jay 服务器的一次性邀请码，登记手机并加密保存访问密钥。
- 给已保存的 Jay 服务器开一个 `127.0.0.1:<端口>` 网关。其他 Android Minecraft Java 启动器如果能访问同一手机的回环端口，可以把这个地址当服务器地址手动连接。
- 网关连接期间启动 Android 前台服务，切到游戏后仍尽量保持回环端口可用。
- 在 Jay 自己的横屏游戏画面里安装并运行 Minecraft 1.21.11，可以创建并进入世界：
  - 第一次打开游戏画面时，点按钮下载经过校验的运行环境（Java 21、MobileGlues 图形桥和 LWJGL，压缩包约 30 MB，装好后约 150 MB）。版本和 SHA-256 固定在 `AndroidRuntimeCatalog.cs`，每个文件都核对后才启用。
  - 再点“安装 Minecraft 1.21.11 并开始游戏”，从 Mojang（或你所在地区默认的下载镜像）下载游戏文件，用和桌面版相同的启动参数在内置 JVM 里启动。
  - 只能用离线玩家名（1–16 个字母、数字或下划线），还不能登录微软账号。
  - 从 Jay 服务器打开游戏时，直接把当前网关地址作为要进的服务器传给游戏。
- 显示 Jay 原创、分辨率无关的触屏控制层：摇杆移动、滑动转向、攻击、使用、跳跃、疾跑、潜行、背包、丢弃、聊天键盘和快捷栏切换。
- APK 内置 Jay 原创的 ARM64/x86_64 JNI 宿主，加载独立 JVM，把 Android `Surface` 和多点触控状态交给 GLFW 桥；两个 ABI 均按 16 KiB 页面构建。
- 跟随系统语言，提供简中、繁中、英文和日文。

## 现在不能做什么

- 只能装和运行 Minecraft 1.21.11 这一个版本，不能装加载器或模组。
- 不能登录微软账号，只能用离线玩家名。
- 只支持 Android 8.0 及以上的 ARM64 和 x86_64 设备，还没在很多真机上测过。
- Jay 不依赖 FCL，也不会复制或移植 FCL/Pojav 的启动器代码。运行组件都单独标许可证、公开源码并固定校验值（见 `third_party/android-*`）。
- 不能创建普通 Jay 房间。EasyTier 2.6.4 没有在本项目中集成 Android 可执行文件。
- PR 的 CI 产物使用测试签名；`android-preview` 发布包使用固定的预览版签名，可覆盖升级并保留数据，但尚未上架 Google Play。

## 本地构建

需要 .NET 10 Android workload、Android SDK、JDK 21 和 Android NDK 28.2.13676358。可按 Microsoft 提供的 `InstallAndroidDependencies` 目标安装与当前项目匹配的依赖：

```sh
dotnet workload install android
dotnet build src/Amber.Android/Amber.Android.csproj \
  -t:InstallAndroidDependencies -f net10.0-android \
  -p:AndroidSdkDirectory=/absolute/path/android-sdk \
  -p:JavaSdkDirectory=/absolute/path/jdk \
  -p:AcceptAndroidSdkLicenses=true

/absolute/path/android-sdk/cmdline-tools/latest/bin/sdkmanager \
  --sdk_root=/absolute/path/android-sdk 'ndk;28.2.13676358'
bash scripts/build-android-native.sh \
  /absolute/path/android-sdk src/Amber.Android/obj/jay-native

dotnet publish src/Amber.Android/Amber.Android.csproj -c Release \
  -p:AndroidSdkDirectory=/absolute/path/android-sdk \
  -p:JavaSdkDirectory=/absolute/path/jdk \
  -p:AndroidPackageFormats=apk
```

产物在 `src/Amber.Android/bin/Release/net10.0-android/publish/`。对外发布前要按 [Avalonia Android 发布文档](https://docs.avaloniaui.net/docs/deployment/android) 配置独立密钥库，密码只能放在 GitHub Secrets 中。

CI 会用 Android Build Tools 的 `zipalign -P 16` 检查 APK，并逐个检查 ARM64 `.so` 的 ELF `LOAD` 段是否至少按 16 KiB 对齐。预览发布使用仓库 Secrets 中的固定签名，后续覆盖安装不会清除玩家的语言、设备身份和已保存服务器。

## 下一步

1. 接入微软账号登录（复用 Amber.Core 的账号模块）。
2. 支持 1.21.11 以外的版本。
3. 在更多 Android 真机上完成从安装运行环境、安装版本、启动到进入服务器的端到端验证。
