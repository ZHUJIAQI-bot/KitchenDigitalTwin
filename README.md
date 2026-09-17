# 焕新家装：装修公司上门维修仿真

> 🎮 **在线体验（网页版，无需安装）**：https://zhujiaqi-bot.github.io/KitchenDigitalTwin/

这是一个面向工程场景数字化展示的 Unity 原型。玩家扮演装修公司员工：先在公司的任务台接单，再前往业主家（含厨房、卫生间、客厅、餐厅、卧室五个房间）逐屋排查并修复问题，控制维修预算。

## 操作方式

- `WASD` / 方向键：移动
- 鼠标左键点击地面：角色自动走过去
- `E`：在任务台接单 / 靠近问题点后维修

## 打开方式（本地运行）

使用 Unity `6000.4.0f1` 打开本目录：

`D:\Codex\2026-09-15\gei\KitchenDigitalTwin`

打开 `Assets/Scenes/Main.unity` 后点击 Play。

如果场景没有自动出现，可以在菜单执行：

`Kitchen > Build Demo Scene`

## 打包

菜单 `Kitchen > Build WebGL` 会把网页版输出到 `docs/` 目录，推送到 GitHub 后由 Pages 自动部署。
