# CloudEmuera

[English](README.md) | 中文

<p align="center"><img src="src/CloudEmuera.Web/public/cloudemuera-icon.png" alt="CloudEmuera 图标" width="180"></p>

CloudEmuera 是一个用于管理和游玩 Era 游戏的自托管浏览器平台。部署一次后，即可从任意设备访问，并使用相同的存档继续游戏。

## CloudEmuera 是什么？

CloudEmuera 将你的 Emuera 游戏集合变成可通过浏览器游玩的游戏空间。每个 Session 都有独立运行的游戏和存档，因此可以分别保留不同的游戏进度，并在之后继续游玩。

它适合希望在自己的服务器上运行游戏、无需在每台设备上安装桌面游戏客户端的用户。

## CloudEmuera 提供什么？

1. **管理完整的 Era 游戏库** — 在一个地方上传、验证、整理和管理所有 Era 游戏。
2. **部署一次，在任意设备上游玩** — 在自己的服务器上运行 CloudEmuera，通过桌面电脑、笔记本电脑、平板电脑或手机访问。
3. **共享存档，随时继续** — 从不同设备访问相同的原生存档，并从上次进度继续游戏。
4. **接近原生的 Emuera 显示效果** — 保留熟悉的控制台式界面，并针对桌面和移动屏幕优化布局。
5. **Emuera 兼容性** — 面向 Emuera 1824+v18 游戏，以及当前 Emuera.EM+EE 兼容性基线构建。

Session 可以创建、关闭、重新打开，并在之后重新连接。多个 Session 彼此独立，管理员可以查看基本的 Session 状态，并在需要时停止 Session。

## 产品预览

以下截图展示主要产品页面和基于浏览器的游戏体验。

### 游戏库

![CloudEmuera 游戏库](img/CloudEmuera_Game.png)

### Session

![CloudEmuera Session](img/CloudEmuera_Sessions.png)

### 存档管理

![CloudEmuera 存档管理](img/CloudEmuera_Saves.png)

### 游戏显示

![CloudEmuera 游戏显示](img/CloudEmuera_Gameplay.png)

### 游戏地图

![CloudEmuera 游戏地图](img/CloudEmuera_Gameplay_Map.png)

## 使用 Docker 一次部署

CloudEmuera 以单个 Docker Compose 服务运行。需要 Docker 28+ 和 Docker Compose v2。

### 首次部署

从仓库根目录创建生产环境文件：

```bash
cd docker
cp .env.example .env
```

启动服务前，编辑 `.env` 并设置首个管理员账户：

```dotenv
CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME=admin
CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL=you@example.com
CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=change-this-password
```

有关 bind mount、网络暴露、HTTPS、备份、更新和生产环境 `.env` 配置说明，请参阅[生产部署指南](docs/deployment.zh-CN.md)。

启动 CloudEmuera：

```bash
docker compose up -d --build
```

在服务器上打开 `http://127.0.0.1:28647`，使用上面配置的账户登录。默认数据保存在 Docker 管理的命名卷 `cloudemuera-data` 中。
首次登录后请修改临时密码。

## 项目状态

CloudEmuera 当前处于独立 MVP 发布准备阶段，面向自托管实例和可信游戏包；不要将当前版本暴露给不可信用户或公网。

## 许可证

CloudEmuera 的原创代码采用 [Apache License 2.0](LICENSE) 授权。Emuera.EM+EE 和其他捆绑组件继续使用其原始许可证，详见 [NOTICE](NOTICE) 和 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
