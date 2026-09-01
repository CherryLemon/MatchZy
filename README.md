# MatchZy · ThuCS 定制版

本目录是 ThuCS Platform 使用的 CounterStrikeSharp MatchZy 定制插件。它基于上游 MatchZy，负责把一台 CS2 游戏服务器变成平台可编排、可观测、可回收的比赛实例。

ThuCS 的房间、BP、参赛资格、实例调度和积分结算仍由主仓库后端负责；MatchZy 负责服务器内的比赛运行时、实时事件和比赛数据采集。两者通过 MatchZy 配置接口、RCON 和 Webhook 连接起来。

相关入口：

- [ThuCS Platform 总览](../README.md)
- [平台架构](../docs/architecture.md)
- [MatchZy 本地调试手册](../docs/matchzy_debug.md)
- [上游 MatchZy 文档](documentation/docs/index.md)

## 当前基线

| 项目 | 当前值 |
| --- | --- |
| 插件版本 | `0.8.15` |
| 运行时 | CounterStrikeSharp API `>= 227` |
| 构建目标 | `.NET 8` |
| 默认聊天前缀 | `[THUCS]` |
| 默认服务器名 | `THUCS \| {TEAM1} vs {TEAM2}` |
| 正式比赛 | 平台下发的单图 5v5 MatchZy 配置 |

## 在 ThuCS 中承担的职责

```text
ThuCS 后端
  ├─ 房间 / BP / 队伍 / 地图 / 参赛资格
  ├─ 分配或复用 CS2 实例
  └─ 通过 RCON 下发 matchzy_loadmatch_url
             │
             ▼
MatchZy
  ├─ 拉取并加载本场配置
  ├─ 热身、准备、拼刀或固定选边、正式比赛
  ├─ 逐回合统计、事件回调、Demo / backup
  └─ series_end / demo_upload_ended
             │
             ▼
ThuCS Webhook
  ├─ 更新实时比分和比赛状态
  ├─ 结算积分与模式统计
  └─ 等待 GOTV 尾部和 Demo 完成后释放实例
```

平台数据库是房间和积分的权威来源，插件内的状态是当前游戏服务器的运行时状态。平台不会把观战者写入 MatchZy 的比赛 spectator 配置，因此观战者只通过 GOTV 获取画面，不会获得比赛服连接。

## ThuCS 定制功能

### 平台配置加载

- 支持 `matchzy_loadmatch_url`，同时保留 `get5_loadmatch_url` 兼容别名；命令只接受服务器控制台调用，玩家不能自行加载比赛。
- 后端通过 RCON 下发配置 URL、header 名称和短时配置凭证。凭证使用 `X-Matchzy-Config-Token` header，不放在 URL query 中。
- 配置加载后，插件会校验并设置队伍、选手、地图、地图侧别、准备门槛、回调地址和比赛 cvar，然后启动热身。
- 支持单图、BO1/多图配置、跳过或执行地图 veto、`knife` / `team1_ct` / `team1_t` 等地图侧别模式。
- 比赛结束后的短暂锁定阶段可以直接接收下一场配置；默认至少保留 30 秒的收尾时间，避免上一场状态污染下一场。

典型的配置加载命令如下，实际命令由 ThuCS 后端通过 RCON 生成并执行：

```text
matchzy_loadmatch_url "https://backend.example/api/matches/123/matchzy-config" "X-Matchzy-Config-Token" "<short-lived-token>"
```

配置接口返回的字段以插件实现为准，ThuCS 当前使用的核心字段包括：

```json
{
  "matchid": 123,
  "team1": { "name": "THUCS A", "players": {} },
  "team2": { "name": "THUCS B", "players": {} },
  "maplist": ["de_mirage"],
  "num_maps": 1,
  "players_per_team": 5,
  "min_players_to_ready": 10,
  "skip_veto": true,
  "map_sides": ["knife"],
  "remote_log_url": "https://backend.example/api/webhook/matchzy",
  "remote_log_header_key": "X-Matchzy-Secret",
  "remote_log_header_value": "<match-scoped-secret>"
}
```

### 热身、准备和开赛

- 热身配置默认启用无限弹药、任意地点购买、暂停热身计时器、关闭自动平衡，并保留较长的准备窗口。
- 热身重生后提供短暂的出生保护，减少玩家刚生成时被立即击杀的问题。
- `.ready` / `.r`、`.unready` / `.ur` 管理准备状态；`.forceready` 供管理员在需要时推进准备阶段。
- 本地 Docker 验收可以通过 `matchzy_local_fill_bots_on_first_connect` 在第一名真人连接后自动补 bot，方便单人验证 `.ready`、warmup 和 `get5_status`。正式平台比赛默认关闭该行为，不能把 bot 当作参赛者。
- 配置比赛后，插件锁定已加载的队伍和选手；非本场玩家不能混入比赛，比赛结束后的锁定阶段也会阻止旧玩家继续留在服务器。

### 拼刀、选边和正式规则

- `map_sides` 为 `knife` 时进入拼刀；若配置为固定侧别，则跳过拼刀并直接使用指定起始阵营。
- `.stay`、`.switch`、`.swap` 用于拼刀后的选边处理。
- 仓库内的 `cfg/MatchZy/live.cfg` 提供 ThuCS 正式比赛基线：5v5、标准回合数、加时、暂停和友伤规则。单场配置下发的 cvar 可以覆盖默认值。
- 正式比赛默认开启白名单、Demo 录制和伤害报告；`.stop` 默认关闭，避免普通玩家通过回合恢复改变已结算比赛。
- 保留练习模式、投掷物回放、bot、轨迹、出生点和位置保存等上游能力，但这些命令属于本地练习/验收工具，不是 ThuCS 房间或 BP 流程的替代入口。

### 逐回合统计和 RWS

ThuCS 定制版在原有击杀、死亡、助攻和伤害统计之外，持续维护以下比赛数据：

- KAST、闪光助攻、首杀/首死、补枪、狙击击杀、爆头和道具伤害；
- 安包、拆包、1K，以及 1v1 至 1v5 残残局尝试和成功次数；
- 按 T / CT 分侧的击杀、死亡、助攻、伤害、首杀/首死、KAST、RWS 和残局数据；
- 每个攻击者-受害者对位的击杀数、总伤害和击杀伤害。

RWS 规则固定为每个有效回合总计 100 分，并且只发放给胜方：

1. 发生安包或拆包时，对应目标玩家先获得 30 分；
2. 剩余分数按胜方玩家对敌方造成的伤害比例分配；
3. 胜方没有造成伤害时，剩余分数在胜方玩家之间均分；
4. 个人跨场和 T/CT 分侧 RWS 都以实际参与回合数归一化。

回合恢复时，插件会同时恢复扩展统计快照和 Valve 回合备份。缺少匹配的扩展快照时不会继续恢复，以免旧 backup 让 KAST、RWS 或残局数据被重复计算。

### Demo、backup 和比赛收尾

- 比赛进入 live 后自动执行 `tv_record`；地图或系列结束时停止录制，并在收尾等待后上传 Demo。
- Demo 默认保存到 `csgo/MatchZy/`，文件名包含时间、比赛 ID、地图和双方队名。
- 可通过 `matchzy_demo_upload_url` 和自定义 header 将 Demo 上传到 ThuCS；成功或失败都会发送 `demo_upload_ended` 事件。
- 逐回合 backup 保存在 `csgo/MatchZyDataBackup/`，同时可通过 `matchzy_remote_backup_url` 上传；backup 包含比赛状态、cvar、Valve 原始备份和扩展统计快照。
- `css_restore` / `.restore` 只允许具备相应管理员权限的操作；`.stop` 是否可用由 `matchzy_stop_command_available` 控制。
- `series_end` 后插件进入收尾/锁定状态，并广播倒计时；平台仍会等待 GOTV 延迟耗尽和 Demo 上传完成，再回收或复用游戏实例。

## 与 ThuCS 后端的回调

### 事件

通过 `remote_log_url` 配置的 HTTP POST 回调会上报 MatchZy 事件。ThuCS 重点消费以下事件：

| 事件 | 用途 |
| --- | --- |
| `series_start` | 确认本场配置已加载并进入热身 |
| `map_picked` / `map_vetoed` / `side_picked` | 记录地图和选边流程 |
| `going_live` | 确认比赛正式开始 |
| `round_end` | 更新比分、逐回合统计和 RWS |
| `map_result` | 记录地图结果及选手对位数据 |
| `series_end` | 触发比赛结束、积分结算和实例收尾 |
| `demo_upload_ended` | 确认 Demo 上传结果 |

事件请求可以配置自定义 header。ThuCS 使用按比赛派生的 `X-Matchzy-Secret` 进行校验；Demo 上传和 Webhook 也使用同一场比赛范围内的凭证。不要把 secret、token 或 header value 拼进 URL query。

### 网络与端口

- MatchZy → ThuCS 配置接口、Webhook 和 Demo 接口应优先走 `BACKEND_INTERNAL_URL` 私网地址。
- 腾讯云生产实例的 RCON 优先走 VPC 内网 IP；公网只承担浏览器可访问的页面和 Demo 下载地址。
- 游戏流量使用实例的 game UDP 端口；GOTV 使用 rcon/TV UDP 映射；Source RCON 使用控制 TCP 端口。具体 host/guest 映射见 [架构文档](../docs/architecture.md) 和 [调试手册](../docs/matchzy_debug.md)。

### 敏感信息处理

- `matchzy_loadmatch_url` 的加载日志会移除 URL query、fragment、用户名和密码，并只记录配置 payload 长度。
- 配置凭证通过 header 传递，不能通过 query 参数传递；Demo、backup 和事件回调的 header 也应使用 header 配置。
- 变更 cvar 时，密码、secret、token 和 header value 等敏感字段不会按明文写入 cvar 诊断日志。
- 排障时应使用比赛 ID、事件名、HTTP 状态码和 `get5_status`；不要在日志、截图或 issue 中粘贴完整配置 JSON、回调 header 或比赛密码。

## 常用命令

命令保留 MatchZy / Get5 的兼容别名；下面只列 ThuCS 联调中常用的命令。

| 范围 | 命令 | 说明 |
| --- | --- | --- |
| 玩家 | `.ready` / `.r` | 准备 |
| 玩家 | `.unready` / `.ur` | 取消准备 |
| 玩家 | `.stay` / `.switch` / `.swap` | 拼刀后选择留在或交换阵营 |
| 玩家 | `.pause` / `.p`、`.unpause` / `.up` | 请求或解除暂停 |
| 管理员 | `.forceready` | 强制推进准备 |
| 管理员 | `.forcepause` / `.fp`、`.forceunpause` / `.fup` | 管理暂停 |
| 管理员 | `.restore <round>` | 恢复到指定回合（需要权限且必须有完整扩展快照） |
| 管理员 | `.asay <message>` | 使用 ThuCS 管理员前缀发送公告 |
| 服务器控制台 | `matchzy_loadmatch_url ...` | 加载平台比赛配置，玩家不可调用 |
| 服务器控制台 | `matchzy_remote_log_*` | 配置事件回调 URL 和 header |
| 服务器控制台 | `matchzy_demo_upload_*` | 配置 Demo 上传 URL 和 header |
| 本地验收 | `matchzy_local_fill_bots_on_first_connect 1` | 第一名真人连接后补本地 bot |

## 配置文件

默认配置位于 `cfg/MatchZy/`，构建时会原样打入 MatchZy 发布包：

| 文件 | 用途 |
| --- | --- |
| `config.cfg` | ThuCS 前缀、白名单、准备、Demo、回调和全局默认值 |
| `warmup.cfg` | 热身购买、无限弹药、出生保护、bot 与暂停计时器 |
| `knife.cfg` | 拼刀阶段的弹药、购买和回合设置 |
| `live.cfg` | 正式比赛的回合、加时、暂停和友伤基线 |
| `live_override.cfg` | 服务器部署需要覆盖的 live cvar |
| `admins.json` | MatchZy 管理员及权限配置；不要将真实 SteamID 提交到公开文档 |

优先通过比赛配置或服务器配置下发 cvar，不要直接修改运行中的 `game/csgo/` live 目录作为长期修复。修改插件后，必须重新构建 package，否则容器或 QEMU 重启会用旧包覆盖手工改动。

## 构建与验收

推荐从主仓库根目录构建，这会同时生成可部署的目录包和 zip 制品：

```bash
BUILD_FRONTEND=0 BUILD_MATCHZY=1 BUILD_DOCKER=0 ./build.sh
```

产物位置：

```text
build/matchzy/package/
build/artifacts/thucs-matchzy-<version>.zip
```

构建脚本会执行 `dotnet publish`、复制 `cfg/`，并校验 DLL、配置文件和 zip 内容。需要构建完整本地 Docker 镜像时再显式设置 `BUILD_DOCKER=1`。

RWS 纯逻辑测试：

```bash
dotnet run --project MatchZy.RwsTests/MatchZy.RwsTests.csproj --configuration Release
```

本地 Docker / QEMU 联调重点检查：

```text
docker logs --tail 320 cs2_server 2>&1 | grep -nE 'LoadMatchFromURL|StartWarmup|SeedLocalFillBotsForWarmup|IsTeamReady|bot_quota'
```

同时用 RCON 查看 `status` 和 `get5_status`：`status` 是真实人类/bot 数量的地面真相，`get5_status` 用于确认 MatchZy 的比赛 ID、地图和 gamestate。更多热更、`.ready`、warmup、bot 和 Demo 排障步骤见 [MatchZy 调试手册](../docs/matchzy_debug.md)。

## 开发与提交约定

`MatchZy/` 是主仓库中的 Git submodule。修改本目录后需要先在子模块提交并推送，再在主仓库提交新的 submodule pointer：

```bash
cd MatchZy
git add README.md
git commit -m "docs: update ThuCS MatchZy README"
git push origin <branch>

cd ..
git add MatchZy
git commit -m "docs: update MatchZy README pointer"
git push origin <branch>
```

不要把只存在于本地 live 目录或 dirty submodule 生成的 DLL 当作正式制品；CI 必须能够从已推送的 submodule commit 重新构建。

## 上游来源与许可证

ThuCS 定制版保留上游 MatchZy 的 MIT 许可证和基于以下项目的实现：

- [MatchZy](https://github.com/shobhit-pathak/MatchZy)
- [Get5](https://github.com/splewis/get5)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- AlliedModders 及 CS2 社区

具体上游实践模式、管理员权限和通用命令说明，请参阅仓库内的 [`documentation/docs/`](documentation/docs/index.md)。
