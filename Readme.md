<p align="center">
	<img src="Res/Icon/PreConnect-iOS-Default-1024x1024@1x.png" alt="PreConnect Logo" width="140" />
</p>

<h1 align="center">PreConnect (PC)</h1>

<p align="center">
	这是 PreConnect 项目的PC部分，一个基于 .NET 8 / WinUI 3 的电脑端主机服务，
	负责高效率采集硬件信息并向移动端提供稳定、轻量的实时数据服务。
</p>

<p align="center">
	核心目标为保持体积小巧，同时实现低占用与高效率的信息获取和服务。
</p>

<p align="center">
	源码公开（Source-Available）｜禁止未经授权的商业使用
</p>

---

## 项目简介

PreConnect (PC) 是 PreConnect 体系中的电脑端 Host，负责：

1. 采集本机硬件遥测数据（温度、负载、功耗、频率、风扇等）。
2. 通过局域网发现与配对能力为移动端建立连接入口。
3. 以轻量 API 提供实时数据与状态信息。

该仓库即 PC 客户端仓库（Windows）。

- PC 客户端仓库：本仓库
- iOS 客户端仓库：<https://github.com/PrelinaMontelli/PreConnect>

---

## 设计目标

1. 小巧体积：减少不必要依赖与复杂组件，保持发布包精简。
2. 低占用：后台运行场景下尽量控制 CPU、内存与 I/O 负担。
3. 高效率：以稳定、快速的方式完成硬件信息采集与 API 响应。
4. 易部署：提供开箱即用的本地运行和打包发布路径。

---

## 核心功能

1. 硬件监控采集：基于 LibreHardwareMonitor 获取多类硬件传感器数据。
2. 局域网发现：通过 UDP 广播/组播发布主机可连接信息。
3. PIN 配对认证：移动端通过配对流程获取会话令牌。
4. 实时遥测接口：授权后轮询获取硬件快照数据。
5. 系统托盘常驻：支持最小化到托盘，适合长期后台运行。

---

## 技术栈

- .NET 8
- C#
- WinUI 3（Windows App SDK）
- ASP.NET Core（Kestrel）
- LibreHardwareMonitor

---

## 快速开始

### 环境要求

- Windows 10/11（建议最新稳定更新）
- .NET SDK 8.0+
- Visual Studio 2022（建议安装 WinUI/桌面开发相关工作负载）

### 本地运行

1. 克隆仓库。
2. 使用 Visual Studio 打开 `PreConnect.sln`。
3. 将 `PreConnect` 设为启动项目。
4. 选择 `x64` + `Debug` 后启动。

### 命令行构建

```bash
dotnet restore PreConnect.sln
dotnet build PreConnect.sln -c Debug
```

如需仅构建主程序项目：

```bash
dotnet build PreConnect/PreConnect.csproj -c Release
```

---

## API 与连接流程（简要）

默认服务端口为 `5005`，典型接口如下：

- `GET /api/ping`：健康检查。
- `GET /api/status`：主机状态与元数据。
- `POST /api/pair`：PIN 配对，成功后返回 session token。
- `GET /api/telemetry`：授权后获取实时遥测快照。

配对完成后，移动端可携带 session token 轮询遥测数据。





## 相关仓库

- iOS 客户端：<https://github.com/PrelinaMontelli/PreConnect>
- LibreHardwareMonitor: <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>

---

## 许可证

本项目使用仓库内 LICENSE.md 指定的许可证。

请注意：

- 本仓库为源码公开项目，不属于 OSI 定义的开源项目。
- 未经作者书面授权，禁止将本项目用于商业用途。
- 具体权利与限制以 LICENSE.md 为准。
