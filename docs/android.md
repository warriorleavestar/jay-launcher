# Jay Android 实验版

Android 版是一个独立的 Avalonia 移动端工程，不会把桌面端的窗口、进程启动和 EasyTier 代码强行带到手机上。

应用显示版本和整数构建号分别跟随 `src/Directory.Build.props` 的 `Version`、`MobileApplicationVersion`，不再需要在 Android 工程和界面中重复修改版本号。

## 现在能做什么

- 在应用私有目录生成并加密保存设备身份。
- 解析 Jay 服务器的一次性邀请码，登记手机并加密保存访问密钥。
- 给已保存的 Jay 服务器开一个 `127.0.0.1:<端口>` 网关。其他 Android Minecraft Java 启动器如果能访问同一手机的回环端口，可以把这个地址当服务器地址手动连接。
- 网关连接期间启动 Android 前台服务，切到游戏后仍尽量保持回环端口可用。
- 打开 Jay 自己的横屏游戏画面，直接把当前网关地址传给内置运行容器。
- 显示 Jay 原创、分辨率无关的触屏控制层：移动、攻击、使用、跳跃、背包、菜单和键盘。
- APK 内置 Jay 原创的 ARM64/x86_64 JNI 宿主，可加载独立 JVM，并把 Android `Surface` 和多点触控状态交给后续图形桥；两个 ABI 均按 16 KiB 页面构建。
- 跟随系统语言，提供简中、繁中、英文和日文。

## 现在不能做什么

- 目前还不能真正启动 Minecraft。原生 JVM 宿主已经接入，但经过校验的 Android OpenJDK、LWJGL 图形桥以及账号/版本启动链尚未完成；游戏画面会明确显示运行环境未安装，不会伪装成可用。
- Jay 不依赖 FCL，也不会复制或移植 FCL/Pojav 的启动器代码。后续运行组件必须单独标许可证、公开源码并固定校验值。
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

1. 给现有原生宿主接入经过校验、许可证和源码信息完整的 Android OpenJDK 与 LWJGL 图形桥。
2. 复用 Amber.Core 的微软账号、版本安装、游戏文件下载和启动参数模块。
3. 在 Android 真机完成从登录、安装版本、启动到进入服务器的端到端验证。
