using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 焕新家装 · 装修公司上门维修仿真
/// 玩家是装修公司员工：先去公司任务台接单，再去业主家逐屋排查修复问题。
/// 采用手动 AABB 碰撞，避免 WebGL 下 CharacterController 物理不可靠。
/// </summary>
public class KitchenSimulator : MonoBehaviour
{
    private const string ProjectName = "焕新家装";
    private const string ProjectSubtitle = "装修公司 · 上门维修";
    private const int StartCash = 600;

    // ── 时间系统：白天(6:00~20:00 共 14 游戏小时)≈ 5 分钟真实时间；7 天为一个月 ──
    private const float RealSecondsPerGameHour = 21.4f;
    private const int HoursPerDay = 24;
    private const int MonthDays = 7;
    private const int MonthSalary = 2200;
    private float gameTime = 8f * RealSecondsPerGameHour;   // 开局第 1 天 08:00
    private const float MoveSpeed = 4.4f;
    private const float InteractDistance = 1.6f;
    private const float RepairDuration = 2.4f;
    private const float MarkerHeight = 1.95f;
    private const float PlayerRadius = 0.34f;

    // 派单节奏
    private const float OrderIntervalMin = 5f;
    private const float OrderIntervalMax = 10f;
    private const int MaxActiveOrders = 6;
    private const int MaxVisibleDone = 3;

    private enum OrderState
    {
        Pending,    // 待维修（红色感叹号）
        Repairing,  // 维修中（黄色）
        Fixed       // 已完工（绿色）
    }

    // 一张维修工单
    private class Order
    {
        public int id;
        public string title;
        public string room;
        public string cause;
        public string plan;
        public int cost;
        public Vector3 site;
        public OrderState state;
        public GameObject marker;
        public TextMesh tag;
        public Renderer[] renderers;
        public float repairProgress;
        public bool needsRebuild;
        public int schedDay;      // 预约：第几天
        public int schedHour;     // 预约：几点
        public ToolKind requiredTool;
        public string sensorName;
        public string sensorUnit;
        public float sensorValue;      // 实时读数
        public float sensorNormal;
        public float sensorAlarm;
        public float sensorMax;
        public float alarmTime;        // 首次报警时刻（用于统计响应时长）
        public readonly List<float> history = new List<float>();   // 最近采样，用于趋势曲线
        public float fixTime;          // 数据回归正常的时刻
        public float normalSince;      // 读数持续正常的起点
        public bool verified;          // 是否通过验收观察期（闭环）
        public bool selfRepairable;    // 是否居民可自修（否则须物业/专业）

        public string Code { get { return "#" + id.ToString("D3"); } }
        public string ScheduleText { get { return "第" + schedDay + "天 " + schedHour.ToString("D2") + ":00"; } }
    }

    // 派单模板：某种房型里可能出现的具体问题，offset 是相对房间中心的偏移
    private class OrderTemplate
    {
        public string roomType;
        public string title;
        public string cause;
        public string plan;
        public int costMin;
        public int costMax;
        public Vector3 offset;
        public ToolKind tool;      // 修这个问题必须使用的工具

        // 数字孪生：该点位挂的传感器
        public string sensorName;
        public string sensorUnit;
        public float sensorNormal;   // 改造后的正常值
        public float sensorAlarm;    // 报警阈值
        public float sensorMax;      // 量程上限（用于进度条）
        public bool selfRepairable;  // 是否居民可自修（否则须物业/专业）

        public OrderTemplate(string roomType, string title, string cause, string plan, int costMin, int costMax, float dx, float dz, ToolKind tool,
            string sensorName, string sensorUnit, float sensorNormal, float sensorAlarm, float sensorMax, bool selfRepairable = true)
        {
            this.tool = tool;
            this.sensorName = sensorName;
            this.sensorUnit = sensorUnit;
            this.sensorNormal = sensorNormal;
            this.sensorAlarm = sensorAlarm;
            this.sensorMax = sensorMax;
            this.selfRepairable = selfRepairable;
            this.roomType = roomType;
            this.title = title;
            this.cause = cause;
            this.plan = plan;
            this.costMin = costMin;
            this.costMax = costMax;
            offset = new Vector3(dx, 0f, dz);
        }
    }

    private class Room
    {
        public string name;     // 例如 "1号楼 厨房"
        public string type;     // 厨房 / 客厅 / 卧室 / 卫生间
        public int building;    // 住宅楼号 1..N；公司=0；宿舍=-1
        public Vector3 center;
        public Vector3 doorPoint;   // 自家入户门口，户主说完要回这里
        public float xMin;
        public float xMax;
        public float zMin;
        public float zMax;
        public bool Contains(Vector3 p)
        {
            return p.x >= xMin && p.x <= xMax && p.z >= zMin && p.z <= zMax;
        }
    }

    // ── 配色 ──────────────────────────────────────────────
    private readonly Color pendingColor = new Color(0.94f, 0.26f, 0.22f);
    private readonly Color workingColor = new Color(1f, 0.76f, 0.18f);
    private readonly Color fixedColor = new Color(0.26f, 0.82f, 0.52f);

    private readonly Color groundColor = new Color(0.32f, 0.36f, 0.34f);
    private readonly Color wallColor = new Color(0.80f, 0.79f, 0.76f);
    private readonly Color interiorWallColor = new Color(0.74f, 0.72f, 0.68f);
    private readonly Color floorA = new Color(0.86f, 0.82f, 0.76f);
    private readonly Color floorB = new Color(0.79f, 0.74f, 0.67f);
    private readonly Color woodColor = new Color(0.48f, 0.31f, 0.17f);
    private readonly Color woodLightColor = new Color(0.62f, 0.44f, 0.27f);
    private readonly Color companyColor = new Color(0.22f, 0.42f, 0.58f);

    private readonly Color panelFill = new Color(0.07f, 0.10f, 0.14f, 0.97f);
    private readonly Color panelBorder = new Color(1f, 1f, 1f, 0.16f);
    private readonly Color dividerColor = new Color(1f, 1f, 1f, 0.10f);
    private readonly Color btnBlue = new Color(0.22f, 0.60f, 0.90f);

    // ── 运行时状态 ────────────────────────────────────────
    private readonly List<Order> orders = new List<Order>();
    private readonly List<OrderTemplate> templates = new List<OrderTemplate>();
    private readonly List<Room> rooms = new List<Room>();
    private readonly Dictionary<int, float[]> buildingBounds = new Dictionary<int, float[]>();   // 楼栋号 → [xMin,xMax,zMin,zMax]
    private readonly List<Bounds> obstacles = new List<Bounds>();
    private readonly List<GameObject> generatedObjects = new List<GameObject>();

    private Material[] stateMaterials;
    private Color[] stateColors;
    private Material wallMaterial;
    private Material glassMaterial;

    // 昼夜与路灯
    private Light sunLight;
    private float sunBaseIntensity = 1.15f;
    private readonly List<Light> lampLights = new List<Light>();
    private readonly List<Light> roomLights = new List<Light>();
    private Material cityWindowMaterial;
    private readonly Color cityWindowDayTone = new Color(0.78f, 0.83f, 0.88f);
    private readonly List<Material> nightLightMaterials = new List<Material>();
    private Material riverMaterial;
    private Material buildingWindowMaterial;
    private readonly List<Renderer> lampGlobes = new List<Renderer>();
    private Material lampOnMaterial;
    private Material lampOffMaterial;
    private bool lampsOn;
    private int lastPaidMonth = 1;
    private int lastDay = 1;

    // 昼夜转场
    private float fadeAlpha;
    private string fadeMessage = string.Empty;
    private int lastPhaseMark = -1;   // 0 白天 1 夜晚，用于检测昼夜切换

    // 日历 / 账目 / 服装 / 商店
    private class LedgerDay
    {
        public int day;
        public int income;
        public int expense;
    }
    private readonly List<LedgerDay> ledger = new List<LedgerDay>();
    private int shopTab;            // 0 工具 1 服装
    private int shopCursor;

    private class Outfit
    {
        public string name;
        public int price;
        public Color coat;
        public Color trouser;
        public bool owned;
    }
    private readonly List<Outfit> outfits = new List<Outfit>();
    private int currentOutfit;
    private readonly List<Renderer> playerClothRenderers = new List<Renderer>();
    private Material playerClothMaterial;
    private Material playerTrouserMaterial;
    private Material floorMaterial;

    private GameObject player;
    private Vector3 playerPosition;
    private Transform playerBody;
    private Transform leftArmPivot;
    private Transform rightArmPivot;
    private Transform leftLegPivot;
    private Transform rightLegPivot;

    private Camera viewCamera;
    private float lookYaw;
    private float lookPitch;
    private bool cursorLocked;
    private const float EyeHeight = 1.52f;
    private const float MouseSensitivity = 2.6f;
    private const float WallHeight = 3.6f;
    private const float DoorHeight = 2.6f;

    // 跳跃：参考地球重力加速度
    private const float Gravity = 9.81f;
    private const float JumpSpeed = 3.9f;   // 起跳高度约 0.78m
    private const float GroundLevel = 0.02f;
    private const float StepInterval = 0.38f;

    private enum ToolKind { Drill, Screwdriver, Wrench, Hammer, Scissors, Tape, Tester }

    private class ToolInfo
    {
        public string name;
        public ToolKind kind;
        public Color color;
        public int price;       // 商店售价
        public bool unlocked;   // 是否已拥有

        public ToolInfo(string name, ToolKind kind, Color color, int price, bool unlocked)
        {
            this.name = name;
            this.kind = kind;
            this.color = color;
            this.price = price;
            this.unlocked = unlocked;
        }
    }

    // 第一人称手持工具（视图模型）
    private Transform toolPivot;
    private float toolAnim;
    private float toolStrike;   // 每次点击左键触发的工具挥动
    private int repairClicks;
    private const int RepairClicks = 4;
    private bool walking;

    // 跳跃
    private float verticalVelocity;
    private bool grounded = true;
    private bool running_;

    // 工具背包
    private readonly List<ToolInfo> tools = new List<ToolInfo>();
    private int currentTool;
    private bool bagOpen;
    private bool minimapLarge;
    private bool shopOpen;
    private bool almanacOpen;
    private bool twinPanelOpen;          // 数字孪生监测面板：默认收起，避免遮挡视野
    private int twinTab;                 // 0 实时数据 / 1 小区俯视图
    private int twinSelectedBuilding;    // 俯视图选中的楼栋（0=未选）
    private Vector2 twinMapScroll;       // 楼栋详情滚动位置
    private int guideStep;               // 新手引导步骤
    private float saveTimer;             // 自动存档计时

    // 玩家住处与睡眠
    private Vector3 sleepPoint;          // 床前站位
    private bool sleeping;
    private float sleepTimer;
    private bool thirdPerson;            // 第三人称视角
    private Transform playerHead;
    private Renderer[] playerHeadRenderers;
    private TextMesh playerNameLabel;
    private bool headVisible = true;
    private const float ThirdPersonDistance = 3.8f;
    private float panelFade;             // 面板展开动效

    // 门
    private class HouseDoor
    {
        public Transform pivot;
        public float open;        // 0 关 / 1 开
        public float target;
        public float xMin;
        public float xMax;
        public float z;
        public bool manualOpen;
    }
    private readonly List<HouseDoor> houseDoors = new List<HouseDoor>();
    private Transform sensorDoorLeft;
    private Transform sensorDoorRight;
    private Vector3 sensorDoorCenter;
    private float sensorDoorHalf;
    private float sensorDoorOpen;

    // 音效（运行时合成，无需音频素材）
    private AudioSource footstepSource;
    private AudioSource voiceSource;
    private AudioClip footstepClip;
    private AudioClip[] voiceBlips;
    private float voiceTimer;
    private float voiceBurst;
    private ParticleSystem dustEffect;
    private AudioSource ambientSource;
    private AudioSource musicSource;
    private bool audioMuted;
    private AudioSource workSource;
    private AudioClip workClip;   // 当前这句还剩多久发声，到 0 就安静下来
    private AudioSource uiSource;
    private AudioSource notifySource;
    private AudioClip uiClickClip;
    private AudioClip notifyClip;
    private float stepTimer;

    private readonly Vector3 spawnPosition = new Vector3(-14.8f, GroundLevel, 0.15f);

    // 开场 NPC 对话
    private class DialogueLine
    {
        public string speaker;
        public string text;
    }
    private readonly List<DialogueLine> dialogue = new List<DialogueLine>();
    private int dialogueIndex = -1;
    private bool introStarted;
    private bool introDone;
    private bool introSeenCloud;   // 云端记录的「是否看过开场嘱托」
    private float introDelay = 1.2f;
    private float typeTimer;
    private const float DialogueTypeTime = 1e6f;   // 单句最大显示时长（用于逐字进度）
    private const float TypeCharsPerSecond = 34f;  // 逐字显示速度
    private Homeowner talkTarget;                  // 对话结束后要登记工单的户主
    private CharacterRig bossRig;

    // 小地图
    private const float WorldMinX = -36f;
    private const float WorldMaxX = 66f;
    private const float WorldMinZ = -20f;
    private const float WorldMaxZ = 28f;

    private Order activeOrder;
    private Order repairingOrder;
    private int orderSerial;
    private float orderTimer;
    private bool taskListExpanded = true;
    private Vector2 taskScroll;
    private int income;     // 完工收入（客户支付）
    private int expenses;   // 购置道具支出
    private string toastText = string.Empty;
    private float toastTimer;
    private string currentRoomName = string.Empty;

    private GUIStyle titleStyle;
    private GUIStyle hintStyle;
    private GUIStyle bodyStyle;
    private GUIStyle smallStyle;
    private GUIStyle buttonStyle;
    private GUIStyle toastStyle;
    private GUIStyle centerStyle;
    private GUIStyle cardTitleStyle;
    private GUIStyle cardButtonStyle;
    private GUIStyle speakerStyle;
    private GUIStyle dialogueStyle;
    private GUIStyle transitionStyle;

    private int Cash { get { return StartCash + income - expenses; } }

    private string startError;
    private bool dialogueJustEnded;

    // ── 登录 / 本地账户 ───────────────────────────────────
    // 说明：GitHub Pages 是静态托管，没有服务端数据库。
    // 这里用 PlayerPrefs（浏览器 localStorage）做本地账户存储，
    // 逻辑集中在 AccountStore 里，将来接真实后端只需替换这一个类。
    private bool loggedIn;
    private string loginUser = "";
    private string loginPass = "";
    private string loginName = "";     // 注册时填写的显示姓名
    private string loginMessage = "";
    private string currentAccount = "";
    private string displayName = "";   // 当前玩家的显示姓名

    // ── Supabase 云端后端 ────────────────────────────────
    // 部署前把这两个常量改成你自己的 Supabase 项目值（控制台 → Project Settings → API）
    private const string SupabaseUrl = "https://cjzjdeojwgpxptfcfoxt.supabase.co";
    private const string SupabaseKey = "sb_publishable_yGVYNaSVQPQCgL3bCTZkbw_q8ZhqONI";
    private bool authBusy;   // 登录/注册请求进行中，防重复提交
    private static bool CloudEnabled { get { return !SupabaseUrl.Contains("YOUR-PROJECT"); } }

    // ── 联机房间 + 聊天 ────────────────────────────────
    private bool inRoom;
    private string roomId = "";
    private string roomCode = "";
    private string joinCode = "";
    private string chatInput = "";
    private string roomMessage = "";
    private readonly List<string> chatMessages = new List<string>();
    private long lastChatId;
    private float presenceTimer;
    private float chatTimer;
    private bool lobbyOpen;
    private bool isHost;
    private float roomSyncTimer;
    private int lastUploadedSignature = int.MinValue;
    private readonly Dictionary<string, RemoteAvatar> remotePlayers = new Dictionary<string, RemoteAvatar>();
    private int loginTab;                 // 0 登录 1 注册 2 外观
    private int custCoat;
    private int custTrouser;
    private Material playerSkinMaterial;

    private static readonly Color[] CoatPalette =
    {
        new Color(0.16f, 0.34f, 0.48f),   // 标准工装蓝
        new Color(0.45f, 0.36f, 0.2f),    // 劳保卡其
        new Color(0.92f, 0.45f, 0.12f),   // 反光橙
        new Color(0.24f, 0.26f, 0.3f),    // 技师深灰
        new Color(0.12f, 0.2f, 0.4f),     // 监理藏青
        new Color(0.5f, 0.18f, 0.18f),    // 枣红夹克
        new Color(0.28f, 0.42f, 0.34f),   // 军绿
        new Color(0.55f, 0.5f, 0.62f),    // 浅紫
    };

    private static readonly Color[] TrouserPalette =
    {
        new Color(0.22f, 0.26f, 0.3f),    // 深蓝灰
        new Color(0.3f, 0.27f, 0.22f),    // 卡其
        new Color(0.16f, 0.17f, 0.19f),   // 近黑
        new Color(0.4f, 0.42f, 0.45f),    // 浅灰
    };

    private static readonly string[] CoatNames = { "工装蓝", "劳保卡其", "反光橙", "技师灰", "监理藏青", "枣红", "军绿", "浅紫" };
    private static readonly string[] TrouserNames = { "深蓝灰", "卡其", "近黑", "浅灰" };

    private static Font uiFont;
    private static bool uiFontLoaded;

    // WebGL 下默认字体不含中文字形，惰性加载打包进来的黑体；失败时静默回退，绝不影响游戏主流程
    private static Font UiFont
    {
        get
        {
            if (!uiFontLoaded)
            {
                uiFontLoaded = true;
                try
                {
                    uiFont = Resources.Load<Font>("simhei");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("中文字体加载失败，回退默认字体：" + e.Message);
                    uiFont = null;
                }
            }
            return uiFont;
        }
    }

    // ── 生命周期 ──────────────────────────────────────────
    private void Start()
    {
        Application.targetFrameRate = 60;
        Time.maximumDeltaTime = 0.1f;
        // WebGL 下限制阴影与逐像素光源开销，避免全屏时掉帧
        QualitySettings.shadowDistance = 30f;
        QualitySettings.shadowCascades = 1;
        QualitySettings.pixelLightCount = 4;

        // 相机最先创建：即使后续初始化抛异常，也能渲染出画面而不是全黑
        BuildCamera();

        try
        {
            BuildMaterials();
            BuildWorld();
            CombineStaticGeometry();
            InitializeOrders();
            ApplyLabelMaterials();
            BuildPlayer();
            BuildNpc();
            // 注意：开场台词在 HandleDialogue 里按延迟构建，不要在这里再建一次，否则会重复两遍
            BuildPlayerHome();
            BuildTools();
            BuildAudio();
            BuildEffects();
        }
        catch (System.Exception e)
        {
            startError = e.GetType().Name + ": " + e.Message;
            Debug.LogError("初始化失败：" + e);
        }

        // 登录界面需要鼠标；登录后再锁定
        SetCursorLock(false);
    }

    private void Update()
    {
        // 每帧复位：防止"按下 E 结束对话"这一帧又被别的交互重新开一场对话（会变成死循环）
        dialogueJustEnded = false;

        UpdateDayNight();
        UpdateFade();
        HandleLook();

        // 未登录：只渲染场景供预览，屏蔽一切操作
        if (!loggedIn)
        {
            return;
        }

        // 自动存档：每 20 秒保存一次
        saveTimer += Time.deltaTime;
        if (saveTimer >= 20f)
        {
            saveTimer = 0f;
            SaveGame();
        }

        // 联机：房间面板开关
        if (Input.GetKeyDown(KeyCode.L) && loggedIn)
        {
            lobbyOpen = !lobbyOpen;
            if (lobbyOpen)
            {
                SetCursorLock(false);   // 打开大厅时召唤鼠标，方便点按钮
            }
        }

        // 维修知识手册开关
        if (Input.GetKeyDown(KeyCode.K) && loggedIn)
        {
            faultManualOpen = !faultManualOpen;
            if (faultManualOpen)
            {
                SetCursorLock(false);
            }
        }

        // 联机轮询：上传我的位置 + 拉取队友位置 + 拉取聊天
        if (inRoom && loggedIn)
        {
            presenceTimer += Time.deltaTime;
            if (presenceTimer >= 1f)
            {
                presenceTimer = 0f;
                UpdatePresence();
                PollPresence();
            }
            chatTimer += Time.deltaTime;
            if (chatTimer >= 1f)
            {
                chatTimer = 0f;
                PollChat();
            }
            roomSyncTimer += Time.deltaTime;
            if (roomSyncTimer >= 2f)
            {
                roomSyncTimer = 0f;
                SyncRoomOrders();
            }
            foreach (var kv in remotePlayers)
            {
                RemoteAvatar av = kv.Value;
                if (av.root == null)
                {
                    continue;
                }
                Vector3 before = av.root.transform.position;
                av.root.transform.position = Vector3.Lerp(before, av.target, Time.deltaTime * 8f);
                av.root.transform.rotation = Quaternion.Slerp(av.root.transform.rotation, Quaternion.Euler(0f, av.targetYaw, 0f), Time.deltaTime * 8f);
                // 走动动画：位置变化就摆臂迈腿
                float moved = Vector3.Distance(before, av.root.transform.position);
                if (moved > 0.004f)
                {
                    av.animTime += Time.deltaTime * 9f;
                }
                else
                {
                    av.animTime = 0f;
                }
                float swing = Mathf.Sin(av.animTime) * 26f;
                SetAvatarLimb(av, "ArmL", Quaternion.Euler(swing, 0f, 0f));
                SetAvatarLimb(av, "ArmR", Quaternion.Euler(-swing, 0f, 0f));
                SetAvatarLimb(av, "LegL", Quaternion.Euler(-swing, 0f, 0f));
                SetAvatarLimb(av, "LegR", Quaternion.Euler(swing, 0f, 0f));
            }
        }

        HandleMovement();
        HandleDialogue();
        UpdateSensors();
        UpdateGuide();
        UpdateSleep();
        UpdateColleagues();
        HandleShopInput();
        UpdateDoors();
        UpdateHomeowners();
        UpdateOrderSpawning();
        DetectInteraction();
        HandleRepairInput();
        UpdateRepair();
        UpdateAnimate();
        UpdateMarkers();
        UpdateCurrentRoom();
        if (toastTimer > 0f)
        {
            toastTimer -= Time.deltaTime;
        }
    }

    // ── 资源 ──────────────────────────────────────────────
    private void BuildMaterials()
    {
        stateColors = new[] { pendingColor, workingColor, fixedColor };
        stateMaterials = new Material[stateColors.Length];
        for (int i = 0; i < stateColors.Length; i++)
        {
            stateMaterials[i] = MakeMaterial(stateColors[i], 0.05f, 0.35f);
        }
        wallMaterial = MakeMaterial(wallColor, 0.02f, 0.4f);
        floorMaterial = MakeMaterial(floorA, 0.02f, 0.35f);
        // 无色透明玻璃：不能带蓝，否则窗玻璃看起来是蓝色的
        glassMaterial = MakeTransparent(new Color(0.94f, 0.96f, 0.97f), 0.10f, 0.95f);
    }

    // ── 世界构建 ──────────────────────────────────────────
    private void BuildWorld()
    {
        RenderSettings.ambientLight = new Color(0.62f, 0.63f, 0.64f);
        RenderSettings.ambientIntensity = 1.1f;
        RenderSettings.fog = false;

        GameObject sunObject = new GameObject("Sun");
        Light sun = sunObject.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = sunBaseIntensity;
        sun.color = new Color(1f, 0.96f, 0.86f);
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = 0.5f;
        sunObject.transform.rotation = Quaternion.Euler(46f, -38f, 0f);
        sunLight = sun;
        generatedObjects.Add(sunObject);

        BuildOutdoor();
        BuildCompany();
        BuildHouse();
    }

    // ── 室外小区环境 ──────────────────────────────────────
    private void BuildOutdoor()
    {
        // 各层顶面高度严格错开，避免共面导致的 z-fighting 闪烁
        CreateDecoCube("Lawn", new Vector3(20f, -0.27f, 0f), new Vector3(360f, 0.5f, 340f), new Color(0.38f, 0.6f, 0.3f));

        Color asphalt = new Color(0.29f, 0.3f, 0.31f);
        Color pavement = new Color(0.68f, 0.68f, 0.66f);
        CreateDecoCube("Road Main", new Vector3(20f, -0.2f, -13f), new Vector3(200f, 0.28f, 7f), asphalt);
        CreateDecoCube("Curb North", new Vector3(20f, -0.02f, -9.2f), new Vector3(200f, 0.16f, 0.6f), pavement);
        CreateDecoCube("Curb South", new Vector3(20f, -0.02f, -16.8f), new Vector3(200f, 0.16f, 0.6f), pavement);
        for (int x = -70; x < 110; x += 9)
        {
            CreateDecoCube("Road Mark", new Vector3(x, -0.04f, -13f), new Vector3(4f, 0.03f, 0.22f), new Color(0.93f, 0.91f, 0.8f));
        }

        // 每栋楼门前的小路
        float[] doorX = { -17f, 5f, 21f, 37f, 53f };
        for (int i = 0; i < doorX.Length; i++)
        {
            CreateDecoCube("Path", new Vector3(doorX[i], -0.06f, -8.3f), new Vector3(2.8f, 0.12f, 2.2f), pavement);
        }
        // 第二排住宅门前小路
        float[] doorX2 = { 13f, 29f, 45f };
        for (int i = 0; i < doorX2.Length; i++)
        {
            CreateDecoCube("Path", new Vector3(doorX2[i], -0.06f, 11.2f), new Vector3(2.8f, 0.12f, 2.2f), pavement);
        }
        // 小区内街（两排楼之间的横向步道）
        CreateDecoCube("Inner Walk", new Vector3(28f, -0.06f, 7.5f), new Vector3(90f, 0.12f, 3f), pavement);

        BuildCitySkyline();
        BuildLujiazui();
        BuildStreetLamps();

        // 绿化：只放在建筑范围之外
        float[] tx = { -14f, -2f, 10f, 26f, 42f, 58f, 68f, -26f, -34f, 16f, 32f, 48f, 64f,
                       -10f, 2f, 14f, 26f, 38f, 50f, 62f, 74f, -22f, 8f, 44f };
        float[] tz = { 9f, 9.5f, 9f, 9.5f, 9f, 9.5f, 9f, 8f, 2f, -1f, -1f, -1f, -1f,
                       -20f, -20.5f, -20f, -20.5f, -20f, -20.5f, -20f, -20f, -19.5f, -20.5f, -20f };
        for (int i = 0; i < tx.Length; i++)
        {
            if (!InsideBuilding(tx[i], tz[i]))
            {
                BuildTree(tx[i], tz[i]);
            }
        }

        // 沿路灌木
        for (int i = 0; i < 22; i++)
        {
            float bx = -26f + i * 5f;
            if (InsideBuilding(bx, -8.2f))
            {
                continue;
            }
            CreateDecoSphere("Bush", new Vector3(bx, 0.3f, -8.2f), 0.45f, MakeMaterial(new Color(0.26f, 0.5f, 0.24f), 0.02f, 0.3f));
        }

        // 云
        BuildCloud(new Vector3(-20f, 17f, 26f), 1.2f);
        BuildCloud(new Vector3(12f, 19f, 34f), 1.5f);
        BuildCloud(new Vector3(-2f, 15.5f, 44f), 1.0f);
        BuildCloud(new Vector3(46f, 20f, 24f), 1.3f);
        BuildCloud(new Vector3(-44f, 18f, 10f), 1.4f);
        BuildCloud(new Vector3(30f, 16.5f, 48f), 1.1f);
        BuildCloud(new Vector3(70f, 18.5f, 30f), 1.2f);
    }

    // 全部建筑的外扩范围，用于避免绿化穿模进屋
    private static readonly float[,] buildingRects =
    {
        { -20f, -8f, -7f, 5f },      // 公司
        { -33f, -22f, -7f, 5f },     // 员工宿舍
        { 2f, 63f, -8f, 8f },        // 第一排住宅（开间进深各不相同，取包络）
        { 10f, 55f, 12f, 27f },      // 第二排住宅
    };

    private static bool InsideBuilding(float x, float z)
    {
        const float margin = 2.4f;
        for (int i = 0; i < buildingRects.GetLength(0); i++)
        {
            if (x > buildingRects[i, 0] - margin && x < buildingRects[i, 1] + margin
                && z > buildingRects[i, 2] - margin && z < buildingRects[i, 3] + margin)
            {
                return true;
            }
        }
        return false;
    }

    // ── 墙体与洞口 ────────────────────────────────────────
    // alongX = true：墙沿 X 方向（固定 Z）；false：墙沿 Z 方向（固定 X）
    // openings 每四个一组：[起点, 终点, 洞底高度, 洞顶高度]
    private void BuildWallWithOpenings(string name, bool alongX, float fixedCoord, float min, float max, float thickness, Color color, params float[] openings)
    {
        float cursor = min;
        for (int i = 0; i + 3 < openings.Length; i += 4)
        {
            float start = openings[i];
            float end = openings[i + 1];
            float bottom = openings[i + 2];
            float top = openings[i + 3];

            if (start > cursor + 0.001f)
            {
                AddWallSegment(name, alongX, fixedCoord, cursor, start, 0f, WallHeight, thickness, color);
            }
            if (bottom > 0.001f)
            {
                AddWallSegment(name, alongX, fixedCoord, start, end, 0f, bottom, thickness, color);
            }
            if (top < WallHeight - 0.001f)
            {
                AddWallSegment(name, alongX, fixedCoord, start, end, top, WallHeight, thickness, color);
            }
            cursor = end;
        }
        if (cursor < max - 0.001f)
        {
            AddWallSegment(name, alongX, fixedCoord, cursor, max, 0f, WallHeight, thickness, color);
        }
    }

    private void AddWallSegment(string name, bool alongX, float fixedCoord, float start, float end, float bottom, float top, float thickness, Color color)
    {
        float length = end - start;
        float height = top - bottom;
        if (length <= 0.001f || height <= 0.001f)
        {
            return;
        }
        float center = (start + end) * 0.5f;
        float y = bottom + height * 0.5f;
        Vector3 position = alongX ? new Vector3(center, y, fixedCoord) : new Vector3(fixedCoord, y, center);
        Vector3 size = alongX ? new Vector3(length, height, thickness) : new Vector3(thickness, height, length);
        AddSolidBox(name, position, size, color);
    }

    private void AddGlassPane(bool alongX, float fixedCoord, float start, float end, float bottom, float top)
    {
        float length = end - start;
        float height = top - bottom;
        float center = (start + end) * 0.5f;
        float y = bottom + height * 0.5f;
        Vector3 position = alongX ? new Vector3(center, y, fixedCoord) : new Vector3(fixedCoord, y, center);
        Vector3 size = alongX ? new Vector3(length, height, 0.04f) : new Vector3(0.04f, height, length);
        CreateDecoCube("Window Glass", position, size, glassMaterial);
        // 窗棂
        Vector3 bar = alongX ? new Vector3(0.05f, height, 0.07f) : new Vector3(0.07f, height, 0.05f);
        CreateDecoCube("Window Mullion", position, bar, new Color(0.55f, 0.53f, 0.5f));
    }

    // ── 本地账户存储（将来替换为真实后端只需改这个类）──────
    private static class AccountStore
    {
        private const string Prefix = "kitchen_account_";

        public static bool Exists(string user)
        {
            return PlayerPrefs.HasKey(Prefix + user.ToLowerInvariant());
        }

        // 简易散列：仅用于避免明文存储，演示项目不做真实加密
        public static string Hash(string input)
        {
            int h = 17;
            for (int i = 0; i < input.Length; i++)
            {
                h = h * 31 + input[i];
            }
            return h.ToString("X8");
        }

        public static void Save(string user, string password, string name, int coat, int trouser)
        {
            string value = Hash(password) + "|" + coat + "|" + trouser + "|" + name;
            PlayerPrefs.SetString(Prefix + user.ToLowerInvariant(), value);
            PlayerPrefs.Save();
        }

        public static bool TryLoad(string user, string password, out string name, out int coat, out int trouser)
        {
            name = "";
            coat = 0;
            trouser = 0;
            string key = Prefix + user.ToLowerInvariant();
            if (!PlayerPrefs.HasKey(key))
            {
                return false;
            }
            string[] parts = PlayerPrefs.GetString(key).Split('|');
            if (parts.Length < 3)
            {
                return false;
            }
            if (parts[0] != Hash(password))
            {
                return false;
            }
            int.TryParse(parts[1], out coat);
            int.TryParse(parts[2], out trouser);
            name = parts.Length > 3 ? parts[3] : "";
            return true;
        }

        public static void UpdateAppearance(string user, int coat, int trouser)
        {
            string key = Prefix + user.ToLowerInvariant();
            if (!PlayerPrefs.HasKey(key))
            {
                return;
            }
            string[] parts = PlayerPrefs.GetString(key).Split('|');
            string hash = parts.Length > 0 ? parts[0] : "0";
            PlayerPrefs.SetString(key, hash + "|" + coat + "|" + trouser);
            PlayerPrefs.Save();
        }
    }

    // ── 静态几何合批：把上千个装饰物按材质合并，draw call 从 1600+ 降到几十 ──
    private void CombineStaticGeometry()
    {
        // 会被逻辑移动的对象不能合并（合并会烘焙变换并销毁原对象，导致门/同事卡死不动）
        HashSet<Transform> protectedRoots = new HashSet<Transform>();
        for (int i = 0; i < colleagues.Count; i++)
        {
            if (colleagues[i].rig != null && colleagues[i].rig.root != null)
            {
                protectedRoots.Add(colleagues[i].rig.root);
            }
        }
        for (int i = 0; i < houseDoors.Count; i++)
        {
            if (houseDoors[i].pivot != null)
            {
                protectedRoots.Add(houseDoors[i].pivot);
            }
        }
        if (sensorDoorLeft != null)
        {
            protectedRoots.Add(sensorDoorLeft);
        }
        if (sensorDoorRight != null)
        {
            protectedRoots.Add(sensorDoorRight);
        }

        MeshFilter[] all = GetComponentsInChildren<MeshFilter>();
        Dictionary<Material, List<MeshFilter>> groups = new Dictionary<Material, List<MeshFilter>>();

        for (int i = 0; i < all.Length; i++)
        {
            MeshFilter filter = all[i];
            if (filter == null || filter.sharedMesh == null)
            {
                continue;
            }
            string objectName = filter.gameObject.name;
            if (objectName.StartsWith("Globe") || objectName.StartsWith("Label")
                || objectName == "Head" || objectName == "Neck" || objectName == "Helmet"
                || objectName == "Hand" || objectName == "Foot")
            {
                continue;   // 灯罩/文字要换材质；角色部件保持独立（双保险）
            }

            bool isProtected = false;
            Transform t = filter.transform;
            while (t != null)
            {
                if (protectedRoots.Contains(t) || t.name.StartsWith("Models/"))
                {
                    isProtected = true;
                    break;
                }
                t = t.parent;
            }
            if (isProtected)
            {
                continue;
            }

            Renderer renderer = filter.GetComponent<Renderer>();
            if (renderer == null || renderer.sharedMaterial == null)
            {
                continue;
            }

            List<MeshFilter> list;
            if (!groups.TryGetValue(renderer.sharedMaterial, out list))
            {
                list = new List<MeshFilter>();
                groups[renderer.sharedMaterial] = list;
            }
            list.Add(filter);
        }

        int batchCount = 0;
        int removed = 0;
        foreach (KeyValuePair<Material, List<MeshFilter>> pair in groups)
        {
            List<MeshFilter> list = pair.Value;
            if (list.Count < 2)
            {
                continue;
            }

            CombineInstance[] instances = new CombineInstance[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                instances[i].mesh = list[i].sharedMesh;
                instances[i].transform = list[i].transform.localToWorldMatrix;
            }

            Mesh mesh = new Mesh();
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.CombineMeshes(instances, true, true);
            mesh.name = "Static Batch " + batchCount;

            GameObject batchObject = new GameObject("Static Batch");
            batchObject.transform.SetParent(transform, false);
            batchObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            batchObject.AddComponent<MeshRenderer>().sharedMaterial = pair.Key;
            generatedObjects.Add(batchObject);

            for (int i = 0; i < list.Count; i++)
            {
                Destroy(list[i].gameObject);
                removed++;
            }
            batchCount++;
        }
        Debug.Log("静态合批完成：合并 " + removed + " 个物件 → " + batchCount + " 个批次");
    }

    // ── 办公室同事 ────────────────────────────────────────
    // ── 玩家住处：公司西侧的单间宿舍（含床，可睡觉）──
    private void BuildPlayerHome()
    {
        const float x0 = -32f, x1 = -23f, z0 = -6f, z1 = 4f;
        const float doorA = -28.6f, doorB = -26.6f;
        Color wall = new Color(0.86f, 0.83f, 0.76f);

        BuildRoomFloor(x0, x1, z0, z1, new Color(0.82f, 0.72f, 0.58f), true);

        BuildWallWithOpenings("Home Front Wall", true, z0, x0, x1, 0.24f, wall, doorA, doorB, 0f, DoorHeight);
        BuildWallWithOpenings("Home Back Wall", true, z1, x0, x1, 0.24f, wall, -30.2f, -27.6f, 0.95f, 2.15f);
        BuildWallWithOpenings("Home Left Wall", false, x0, z0, z1, 0.24f, wall, -3.6f, -1.2f, 0.95f, 2.15f);
        BuildWallWithOpenings("Home Right Wall", false, x1, z0, z1, 0.24f, wall, -3.6f, -1.2f, 0.95f, 2.15f);
        AddGlassPane(true, z1 - 0.14f, -30.2f, -27.6f, 0.95f, 2.15f);
        AddGlassPane(false, x0 + 0.14f, -3.6f, -1.2f, 0.95f, 2.15f);
        AddGlassPane(false, x1 - 0.14f, -3.6f, -1.2f, 0.95f, 2.15f);

        BuildRoof("Home Roof", (x0 + x1) * 0.5f, (z0 + z1) * 0.5f, x1 - x0, z1 - z0, new Color(0.46f, 0.36f, 0.3f));

        // 室内陈设：床头靠北墙、衣柜靠西墙、书桌椅
        BuildBed(-28.4f, z1 - 1.55f, 180f);
        BuildWardrobe(x0 + 0.55f, 0.9f, 90f);
        BuildTable(-25.4f, 1.5f, 1.25f, 0.75f, 0f, 0.74f);
        BuildChair(-25.4f, 0.55f, 0f);
        BuildPlant(x1 - 0.7f, -4.7f);
        BuildCeilingLight(-27.5f, -0.6f);
        AddRoomLight(-27.5f, -0.6f, 14f);

        BuildHouseDoor("Home Door", doorA, doorB, z0);

        CreateDecoCube("Home Plate", new Vector3(-24.3f, 1.75f, z0 - 0.19f), new Vector3(1.7f, 0.6f, 0.08f), new Color(0.3f, 0.22f, 0.16f));
        CreateWorldLabel("员工宿舍", new Vector3(-24.3f, 1.75f, z0 - 0.34f), 0.055f, Color.white);

        sleepPoint = new Vector3(-28.4f, GroundLevel, z1 - 3.1f);
        AddRoom("员工宿舍", "宿舍", -1, x0, x1, z0, z1, new Vector3((doorA + doorB) * 0.5f, GroundLevel, z0 + 1.6f));
    }

    private void BuildColleagues()
    {
        BuildDeskStation(-17.6f, -1.0f, 0f, true);
        BuildDeskStation(-17.6f, 2.2f, 0f, true);
        BuildColleague(-17.6f, -0.05f, 180f, "老王", new Color(0.5f, 0.45f, 0.28f),
            new[] { "这户的水路我看过，八成是角阀老化。", "记账别忘了，月底要对账的。", "累了就歇会儿，活儿是干不完的。" });
        BuildColleague(-12.0f, -0.05f, 180f, "小李", new Color(0.28f, 0.42f, 0.4f),
            new[] { "陈哥，新来那批工具箱在仓库左边。", "客户催得紧的话，先打个电话说一声。", "我刚学了个补墙的新做法，回头教你。" });
        BuildColleague(-17.6f, 3.15f, 180f, "老赵", new Color(0.42f, 0.3f, 0.42f),
            new[] { "天黑路灯就亮，夜班注意脚下。", "工资发了？去服装店看看新工装。", "工具买齐了干活快，别舍不得花钱。" });
    }

    private void BuildColleague(float x, float z, float yaw, string name, Color coat, string[] lines)
    {
        // 坐姿：髋部下沉到椅面高度（椅面 0.47m - 髋部局部 0.62m = 根节点 -0.15）
        // 大腿前伸 -55°、小腿回正 +55°，脚正好落地
        CharacterRig rig = BuildCharacterModel(name, new Vector3(x, -0.15f, z), yaw, coat, new Color(0.85f, 0.68f, 0.52f));
        rig.leftLeg.localRotation = Quaternion.Euler(-55f, 0f, 0f);
        rig.rightLeg.localRotation = Quaternion.Euler(-55f, 0f, 0f);
        rig.leftKnee.localRotation = Quaternion.Euler(55f, 0f, 0f);
        rig.rightKnee.localRotation = Quaternion.Euler(55f, 0f, 0f);
        rig.leftArm.localRotation = Quaternion.Euler(-74f, 0f, 0f);
        rig.rightArm.localRotation = Quaternion.Euler(-74f, 0f, 0f);
        colleagues.Add(new Colleague
        {
            name = name, rig = rig, lines = lines,
            seat = new Vector3(x, -0.15f, z), seatYaw = yaw, state = 0
        });
    }

    // 站姿（下班走路时用）
    private void SetColleagueStanding(Colleague c)
    {
        c.rig.root.position = new Vector3(c.rig.root.position.x, 0f, c.rig.root.position.z);
        c.rig.leftLeg.localRotation = Quaternion.identity;
        c.rig.rightLeg.localRotation = Quaternion.identity;
        c.rig.leftKnee.localRotation = Quaternion.identity;
        c.rig.rightKnee.localRotation = Quaternion.identity;
        c.rig.leftArm.localRotation = Quaternion.identity;
        c.rig.rightArm.localRotation = Quaternion.identity;
    }

    // 坐姿（回到工位后恢复）
    private void SetColleagueSeated(Colleague c)
    {
        c.rig.root.position = c.seat;
        c.rig.root.rotation = Quaternion.Euler(0f, c.seatYaw, 0f);   // 关键：恢复面朝工位
        c.rig.leftLeg.localRotation = Quaternion.Euler(-55f, 0f, 0f);
        c.rig.rightLeg.localRotation = Quaternion.Euler(-55f, 0f, 0f);
        c.rig.leftKnee.localRotation = Quaternion.Euler(55f, 0f, 0f);
        c.rig.rightKnee.localRotation = Quaternion.Euler(55f, 0f, 0f);
        c.rig.leftArm.localRotation = Quaternion.Euler(-74f, 0f, 0f);
        c.rig.rightArm.localRotation = Quaternion.Euler(-74f, 0f, 0f);
    }

    // 走向目标点，到达返回 true
    private bool WalkTo(Colleague c, Vector3 target, float speed)
    {
        Vector3 current = c.rig.root.position;
        Vector3 flat = new Vector3(target.x, current.y, target.z);
        Vector3 delta = flat - current;
        delta.y = 0f;

        if (delta.magnitude <= 0.16f)
        {
            c.rig.root.position = flat;
            return true;
        }
        c.rig.root.position += delta.normalized * speed * Time.deltaTime;
        Vector3 face = new Vector3(delta.x, 0f, delta.z);
        if (face.sqrMagnitude > 0.0004f)
        {
            c.rig.root.rotation = Quaternion.Slerp(c.rig.root.rotation, Quaternion.LookRotation(face), Time.deltaTime * 8f);
        }
        AnimateRig(c.rig, true, 1f);
        return false;
    }

    // 天黑下班、天亮返岗
    private void UpdateColleagues()
    {
        bool night = IsNight;
        for (int i = 0; i < colleagues.Count; i++)
        {
            Colleague c = colleagues[i];
            if (c.rig == null || c.rig.root == null)
            {
                continue;
            }

            if (night && c.state == 0)
            {
                c.state = 1;
                c.walkTimer = 0f;
                SetColleagueStanding(c);
                ShowToast(c.name + " 下班回家了", 3f);
            }
            else if (!night && c.state == 2)
            {
                c.state = 3;
                c.walkTimer = 0f;
                c.rig.root.gameObject.SetActive(true);
                SetColleagueStanding(c);
            }

            c.walkTimer += Time.deltaTime;

            switch (c.state)
            {
                case 1:
                    // 兜底：走路超过 25 秒直接判定已到家，避免卡住死循环
                    if (c.walkTimer > 25f)
                    {
                        c.state = 2;
                        c.rig.root.gameObject.SetActive(false);
                        break;
                    }
                    // 先走到公司门口，再走出门外
                    if (WalkTo(c, new Vector3(-15.6f, 0f, -6.4f), 2.4f))
                    {
                        if (WalkTo(c, new Vector3(-16.4f, 0f, -11.5f), 2.4f))
                        {
                            c.state = 2;
                            c.rig.root.gameObject.SetActive(false);   // 到家，离场
                        }
                    }
                    break;
                case 3:
                    // 兜底：返岗走路超过 25 秒直接归位
                    if (c.walkTimer > 25f)
                    {
                        SetColleagueSeated(c);
                        c.state = 0;
                        break;
                    }
                    if (WalkTo(c, c.seat, 2.4f))
                    {
                        SetColleagueSeated(c);
                        c.state = 0;
                    }
                    break;
            }
        }
    }

    private void StartColleagueChat(Colleague colleague)
    {
        talkTarget = null;
        dialogue.Clear();
        dialogue.Add(new DialogueLine { speaker = colleague.name, text = Pick(colleague.lines) });
        dialogue.Add(new DialogueLine { speaker = "陈师傅", text = Pick(Replies) });
        dialogueIndex = 0;
        BeginLine();
    }

    // ── 小区路灯（天黑自动亮）──────────────────────────────
    // ══ 远处「陆家嘴」式超高层天际线 ══
    private Material NightLight(Color c)
    {
        Material m = MakeMaterial(c, 0.1f, 0.65f);
        m.DisableKeyword("_EMISSION");
        nightLightMaterials.Add(m);
        return m;
    }

    private void BuildLujiazui()
    {
        Vector3 c = new Vector3(30f, 0f, -92f);

        // 江面（黄浦江）——自定义水面着色器（透明 + 菲涅尔反光 + 波纹 + 夜间泛光）
        Shader waterShader = Shader.Find("Custom/Water");
        if (waterShader != null)
        {
            riverMaterial = new Material(waterShader);
            riverMaterial.SetColor("_DeepColor", new Color(0.03f, 0.14f, 0.28f, 1.0f));
            riverMaterial.SetColor("_ShallowColor", new Color(0.08f, 0.30f, 0.50f, 1.0f));
        }
        else
        {
            riverMaterial = MakeMaterial(new Color(0.10f, 0.30f, 0.48f), 0.2f, 0.7f);
        }
        CreateWaterSurface("River", new Vector3(30f, 0.02f, -42f), 320f, 26f, riverMaterial);
        // 江面是不可跨越的边界：加隐形碰撞体，玩家走到江边会被挡下而不是「踩」在水面上
        obstacles.Add(new Bounds(new Vector3(30f, 1f, -42f), new Vector3(320f, 2f, 26f)));

        SpawnLandmark("Models/Lujiazui/OrientalPearl", c + new Vector3(-38f, 0f, 12f));
        SpawnLandmark("Models/Lujiazui/ShanghaiTower", c + new Vector3(-10f, 0f, -4f));
        SpawnLandmark("Models/Lujiazui/SWFC", c + new Vector3(10f, 0f, 3f));
        SpawnLandmark("Models/Lujiazui/JinMao", c + new Vector3(28f, 0f, -6f));

        // 周边高层群——发光窗格楼体，形成密集的现代化夜城天际线（置于地标之后）
        Shader winShader = Shader.Find("Custom/BuildingWindows");
        buildingWindowMaterial = winShader != null
            ? new Material(winShader)
            : MakeMaterial(new Color(0.28f, 0.32f, 0.4f), 0.1f, 0.5f);
        Color[] lightTones =
        {
            new Color(0.35f, 0.75f, 1f), new Color(0.75f, 0.45f, 1f),
            new Color(1f, 0.75f, 0.35f), new Color(1f, 0.4f, 0.45f),
            new Color(0.4f, 1f, 0.85f),
        };
        System.Random rng = new System.Random(2026);
        for (int i = 0; i < 16; i++)
        {
            float x = c.x - 80f + i * 11f + (float)rng.NextDouble() * 5f;
            float z = c.z - 24f - (float)rng.NextDouble() * 38f;
            float h = 24f + (float)rng.NextDouble() * 28f;
            float w = 7f + (float)rng.NextDouble() * 6f;
            CreateDecoCube("LJ Tower", new Vector3(x, h * 0.5f - 0.2f, z), new Vector3(w, h, w * 0.85f), buildingWindowMaterial);
            // 顶部灯冠
            Material light = NightLight(lightTones[rng.Next(lightTones.Length)]);
            CreateDecoCube("LJ Crown", new Vector3(x, h + 0.6f, z), new Vector3(w * 1.05f, 1.2f, w * 0.9f), light);
        }
    }

    // 加载 Blender 导出的地标模型（Resources），并收集发光材质供夜间点灯
    private void SpawnLandmark(string path, Vector3 position)
    {
        GameObject prefab = Resources.Load<GameObject>(path);
        if (prefab == null)
        {
            Debug.LogWarning("未找到地标模型: " + path);
            return;
        }
        // 注意：FBX 根节点自带 Blender→Unity 的轴转换旋转(270°X)和缩放(100x)，
        // 必须保留其变换、只改位置；若用 Quaternion.identity 覆盖会导致模型横躺。
        GameObject instance = Instantiate(prefab, transform);
        instance.transform.localPosition = position;
        instance.name = path;
        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Material[] mats = renderers[i].sharedMaterials;
            for (int j = 0; j < mats.Length; j++)
            {
                Material m = mats[j];
                if (m != null && m.name.Contains("LJ_Glow"))
                {
                    m.DisableKeyword("_EMISSION");
                    nightLightMaterials.Add(m);
                }
            }
        }
        generatedObjects.Add(instance);
    }

    // 生成细分水面网格（供水面着色器的顶点波纹使用）
    private GameObject CreateWaterSurface(string name, Vector3 center, float width, float depth, Material material)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.position = center;

        int segX = 128, segZ = 28;
        Vector3[] verts = new Vector3[(segX + 1) * (segZ + 1)];
        Vector2[] uv = new Vector2[verts.Length];
        for (int z = 0; z <= segZ; z++)
        {
            for (int x = 0; x <= segX; x++)
            {
                int idx = z * (segX + 1) + x;
                verts[idx] = new Vector3((x / (float)segX - 0.5f) * width, 0f, (z / (float)segZ - 0.5f) * depth);
                uv[idx] = new Vector2(x / (float)segX, z / (float)segZ);
            }
        }
        int[] tris = new int[segX * segZ * 6];
        int t = 0;
        for (int z = 0; z < segZ; z++)
        {
            for (int x = 0; x < segX; x++)
            {
                int i0 = z * (segX + 1) + x;
                int i1 = i0 + 1;
                int i2 = i0 + segX + 1;
                int i3 = i2 + 1;
                tris[t++] = i0; tris[t++] = i2; tris[t++] = i1;
                tris[t++] = i1; tris[t++] = i2; tris[t++] = i3;
            }
        }
        Mesh mesh = new Mesh();
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.uv = uv;
        mesh.RecalculateNormals();

        MeshFilter mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = material;
        generatedObjects.Add(go);
        return go;
    }

    private void BuildStreetLamps()
    {
        // 灯罩自发光：点光源在灯罩内部，照不到球体外表面，必须靠 emission 才会"亮"
        lampOnMaterial = MakeGlow(new Color(1f, 0.9f, 0.62f), 1.6f);
        lampOffMaterial = MakeMaterial(new Color(0.42f, 0.44f, 0.46f), 0.1f, 0.4f);
        Material pole = MakeMaterial(new Color(0.3f, 0.32f, 0.34f), 0.6f, 0.55f);

        // 沿主干道与小区内街布点，按需稀疏
        float[] xs = { -12f, 6f, 24f, 42f, 60f };
        for (int i = 0; i < xs.Length; i++)
        {
            BuildLamp(new Vector3(xs[i], 0f, -10.4f), pole);
            BuildLamp(new Vector3(xs[i] + 9f, 0f, 9.6f), pole);
        }
    }

    private void BuildLamp(Vector3 basePosition, Material pole)
    {
        GameObject lamp = new GameObject("Street Lamp");
        lamp.transform.SetParent(transform, false);
        lamp.transform.position = basePosition;

        DecoPart(PrimitiveType.Cylinder, "Base", lamp.transform, new Vector3(0f, 0.15f, 0f), new Vector3(0.22f, 0.15f, 0.22f), Quaternion.identity, pole);
        DecoPart(PrimitiveType.Cylinder, "Pole", lamp.transform, new Vector3(0f, 2.1f, 0f), new Vector3(0.08f, 2.1f, 0.08f), Quaternion.identity, pole);
        DecoPart(PrimitiveType.Cube, "Arm", lamp.transform, new Vector3(0.35f, 4.12f, 0f), new Vector3(0.8f, 0.09f, 0.09f), Quaternion.identity, pole);

        GameObject globe = DecoPart(PrimitiveType.Sphere, "Globe", lamp.transform, new Vector3(0.72f, 3.98f, 0f), Vector3.one * 0.42f, Quaternion.identity, lampOffMaterial);
        lampGlobes.Add(globe.GetComponent<Renderer>());

        GameObject lightObject = new GameObject("Lamp Light");
        lightObject.transform.SetParent(lamp.transform, false);
        lightObject.transform.position = basePosition + new Vector3(0.72f, 3.9f, 0f);
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = 16f;
        light.intensity = 2.2f;
        light.color = new Color(1f, 0.9f, 0.68f);
        light.enabled = false;
        lampLights.Add(light);

        generatedObjects.Add(lamp);
    }

    // 时间推进 + 昼夜变化
    private void UpdateDayNight()
    {
        float previousTime = gameTime;
        gameTime += Time.deltaTime;

        // 6 点日出、18 点日落
        float dayFactor = Mathf.Clamp01(Mathf.Sin((GameHourFloat - 6f) / 12f * Mathf.PI));
        Color nightSky = new Color(0.06f, 0.09f, 0.17f);
        Color daySky = new Color(0.40f, 0.68f, 0.98f);
        Color duskSky = new Color(0.86f, 0.52f, 0.32f);

        // 黄昏/清晨的暖色过渡：日出前后与日落前后各约 1.5 小时
        float sunrise = Mathf.Clamp01(1f - Mathf.Abs(GameHourFloat - 6.5f) / 1.5f);
        float sunset = Mathf.Clamp01(1f - Mathf.Abs(GameHourFloat - 18f) / 1.5f);
        float duskWarmth = Mathf.Max(sunrise, sunset);

        if (viewCamera != null)
        {
            Color sky = Color.Lerp(nightSky, daySky, dayFactor);
            viewCamera.backgroundColor = Color.Lerp(sky, duskSky, duskWarmth * 0.55f);
        }
        if (sunLight != null)
        {
            sunLight.intensity = Mathf.Lerp(0.2f, sunBaseIntensity, dayFactor);
            Color sunTone = Color.Lerp(new Color(0.62f, 0.72f, 1f), new Color(1f, 0.96f, 0.86f), dayFactor);
            sunLight.color = Color.Lerp(sunTone, new Color(1f, 0.58f, 0.3f), duskWarmth * 0.8f);
            // 太阳高度角：正午最高约 62°，晨昏接近地平线约 12°
            // （Unity 平行光 forward 为 +Z，rotation.x = 90° 才是垂直向下）
            float sunPitch = Mathf.Lerp(12f, 62f, dayFactor);
            sunLight.transform.rotation = Quaternion.Euler(sunPitch, -38f, 0f);
        }
        // 夜间环境光不能压太暗，否则合批后的大网格几乎全黑
        RenderSettings.ambientLight = Color.Lerp(new Color(0.34f, 0.38f, 0.48f), new Color(0.78f, 0.80f, 0.82f), dayFactor);
        RenderSettings.ambientIntensity = Mathf.Lerp(0.78f, 1.1f, dayFactor);

        // 昼夜切换 → 转场画面
        int phaseMark = IsNight ? 1 : 0;
        if (lastPhaseMark < 0)
        {
            lastPhaseMark = phaseMark;
        }
        else if (phaseMark != lastPhaseMark)
        {
            lastPhaseMark = phaseMark;
            StartTransition(phaseMark == 1 ? "夜幕降临" : "天亮了");
        }

        bool shouldLightUp = dayFactor < 0.22f;
        if (shouldLightUp != lampsOn)
        {
            lampsOn = shouldLightUp;
            for (int i = 0; i < lampLights.Count; i++)
            {
                lampLights[i].enabled = lampsOn;
            }
            for (int i = 0; i < lampGlobes.Count; i++)
            {
                if (lampGlobes[i] != null)
                {
                    lampGlobes[i].sharedMaterial = lampsOn ? lampOnMaterial : lampOffMaterial;
                }
            }
            // 室内灯：白天全灭
            for (int i = 0; i < roomLights.Count; i++)
            {
                roomLights[i].enabled = lampsOn;
            }
            // 陆家嘴式地标灯光
            for (int i = 0; i < nightLightMaterials.Count; i++)
            {
                Material m = nightLightMaterials[i];
                if (lampsOn)
                {
                    m.EnableKeyword("_EMISSION");
                    m.SetColor("_EmissionColor", m.color * 2.0f);
                }
                else
                {
                    m.DisableKeyword("_EMISSION");
                }
            }
            // 水面夜间泛光
            if (riverMaterial != null)
            {
                riverMaterial.SetFloat("_Glow", lampsOn ? 1f : 0f);
            }
            // 楼体窗格夜间亮灯
            if (buildingWindowMaterial != null)
            {
                buildingWindowMaterial.SetFloat("_Glow", lampsOn ? 1f : 0f);
            }
            // 远处城市的窗户亮起（假装屋里开着灯）
            if (cityWindowMaterial != null)
            {
                if (lampsOn)
                {
                    cityWindowMaterial.EnableKeyword("_EMISSION");
                    cityWindowMaterial.SetColor("_EmissionColor", new Color(1f, 0.84f, 0.55f) * 1.15f);
                    cityWindowMaterial.color = new Color(1f, 0.9f, 0.66f);
                }
                else
                {
                    cityWindowMaterial.DisableKeyword("_EMISSION");
                    cityWindowMaterial.color = cityWindowDayTone;
                }
            }
        }

        // 跨日 / 跨月结算
        int day = DayIndex;
        if (day != lastDay)
        {
            lastDay = day;
            if (GameMonth > lastPaidMonth)
            {
                lastPaidMonth = GameMonth;
                PaySalary();
            }
        }
    }

    // ── 账目本 ────────────────────────────────────────────
    private LedgerDay Today()
    {
        int day = DayIndex;
        for (int i = 0; i < ledger.Count; i++)
        {
            if (ledger[i].day == day)
            {
                return ledger[i];
            }
        }
        LedgerDay entry = new LedgerDay { day = day };
        ledger.Add(entry);
        return entry;
    }

    private void AddIncome(int amount)
    {
        income += amount;
        Today().income += amount;
    }

    private void AddExpense(int amount)
    {
        expenses += amount;
        Today().expense += amount;
    }

    // 上床睡觉（参考沙盒游戏：天黑睡觉直接跳到次日清晨）
    private void StartSleep()
    {
        if (!IsNight)
        {
            ShowToast("天还亮着，先干活吧 —— 天黑后回宿舍睡觉", 3.5f);
            return;
        }
        sleeping = true;
        sleepTimer = 2.2f;
        StartTransition("就寝中…");
    }

    private void UpdateSleep()
    {
        if (!sleeping)
        {
            return;
        }
        sleepTimer -= Time.deltaTime;
        if (sleepTimer > 0f)
        {
            return;
        }

        sleeping = false;
        float hoursNow = gameTime / RealSecondsPerGameHour;
        float nextMorning = (Mathf.Floor(hoursNow / HoursPerDay) + 1f) * HoursPerDay + 6f;
        gameTime = nextMorning * RealSecondsPerGameHour;
        lastDay = DayIndex;
        lastPhaseMark = 0;
        StartTransition("第 " + GameMonth + " 月 第 " + DayOfMonth + " 天 · 清晨");
    }

    // 转场淡入淡出
    private void StartTransition(string message)
    {
        fadeAlpha = 1f;
        fadeMessage = message;
    }

    // 传感器实时数据仿真：报警值带噪声波动；改造完成后回落到正常值
    private float sampleTimer;
    private const float ObserveHours = 3f;   // 验收观察期（工程小时，约 21 秒实时）

    private void UpdateSensors()
    {
        float dt = Time.deltaTime;
        sampleTimer += dt;
        bool doSample = sampleTimer >= 0.25f;
        if (doSample)
        {
            sampleTimer = 0f;
        }
        for (int i = 0; i < orders.Count; i++)
        {
            Order order = orders[i];
            bool fixedOrder = order.state == OrderState.Fixed;
            float target = fixedOrder ? order.sensorNormal : order.sensorAlarm * 1.25f;

            float noise = 1f
                + Mathf.Sin(Time.time * 2.1f + i * 1.7f) * 0.035f
                + Mathf.Sin(Time.time * 7.3f + i * 0.9f) * 0.012f;

            order.sensorValue = Mathf.Lerp(order.sensorValue, target, dt * (fixedOrder ? 1.6f : 0.7f)) * noise;

            // 约 4Hz 采样一次，保留最近 48 点作为趋势曲线
            if (doSample)
            {
                order.history.Add(order.sensorValue);
                if (order.history.Count > 48)
                {
                    order.history.RemoveAt(0);
                }
            }

            // 记录"数据回归正常"的时刻，用于统计处置时长
            if (fixedOrder && order.fixTime <= 0f
                && Mathf.Abs(order.sensorValue - order.sensorNormal) < Mathf.Max(0.05f, order.sensorAlarm * 0.08f))
            {
                order.fixTime = gameTime;
                order.normalSince = gameTime;
            }

            // 验收判据：读数稳定低于报警阈值并持续 OBSERVE_HOURS 游戏小时 → 判定闭环
            if (fixedOrder && !order.verified && order.fixTime > 0f)
            {
                if (order.sensorValue < order.sensorAlarm)
                {
                    if (order.normalSince <= 0f)
                    {
                        order.normalSince = gameTime;
                    }
                    if ((gameTime - order.normalSince) / RealSecondsPerGameHour >= ObserveHours)
                    {
                        order.verified = true;
                        ShowToast("闭环验收通过 · " + order.Code + " " + order.room
                            + "　读数稳定低于阈值 " + ObserveHours.ToString("F0") + " 小时", 5f);
                        SaveGame();
                    }
                }
                else
                {
                    order.normalSince = 0f;   // 数据反弹则重新计时
                }
            }
        }
    }

    // ── KPI 统计 ──────────────────────────────────────────
    private int TotalOrders() { return orderSerial; }

    private int HazardTotal() { return orders.Count; }

    // 隐患消除率（%）
    private float HazardClearRate()
    {
        if (orders.Count == 0) return 100f;
        return (float)CountFixed() / orders.Count * 100f;
    }

    // 平均处置时长（游戏小时）：从报警到数据回归正常
    private float AverageResponseHours()
    {
        float sum = 0f;
        int n = 0;
        for (int i = 0; i < orders.Count; i++)
        {
            if (orders[i].fixTime > 0f)
            {
                sum += (orders[i].fixTime - orders[i].alarmTime) / RealSecondsPerGameHour;
                n++;
            }
        }
        return n == 0 ? 0f : sum / n;
    }

    // 传统人工巡检的基准（用于对比分析）
    private float LegacyMissRate() { return 32f; }      // 漏检率
    private float LegacyResponseHours() { return 26f; } // 平均处置时长
    private float LegacyCostFactor() { return 1.28f; }  // 成本系数

    // ── 逐级解锁：完成工单数驱动楼栋扩张与职称晋升 ──────
    private const int OrdersPerBuilding = 3;   // 每完成 3 单解锁下一栋楼
    private const int ResidenceCount = 5;      // 住宅总栋数（须与 BuildHouse 一致）

    // 当前已解锁的住宅楼栋数（一栋起步）
    private int UnlockedBuildingCount()
    {
        return Mathf.Clamp(1 + CountFixed() / OrdersPerBuilding, 1, ResidenceCount);
    }

    private bool IsBuildingUnlocked(int building)
    {
        return building <= UnlockedBuildingCount();
    }

    // 职称：随累计完工数晋升
    private string RankTitle()
    {
        int done = CountFixed();
        if (done >= 12) return "首席技师";
        if (done >= 9) return "维修专家";
        if (done >= 6) return "高级技师";
        if (done >= 3) return "熟练维修工";
        return "学徒维修工";
    }

    // 工单归属：优先按标题查模板（权威值），旧存档缺字段时兜底
    private bool OrderSelfRepairable(Order order)
    {
        for (int i = 0; i < templates.Count; i++)
        {
            if (templates[i].title == order.title)
            {
                return templates[i].selfRepairable;
            }
        }
        return order.selfRepairable;
    }

    // ── 新手引导：按步骤提示，评审可快速理解系统逻辑 ──
    private void UpdateGuide()
    {
        if (!loggedIn)
        {
            return;
        }
        switch (guideStep)
        {
            case 0:
                if (twinPanelOpen) guideStep = 1;
                break;
            case 1:
                if (CountFixed() > 0) guideStep = 2;
                break;
            case 2:
                if (CountVerified() > 0) guideStep = 3;
                break;
            case 3:
                if (UnlockedBuildingCount() > 1) guideStep = 4;
                break;
            case 4:
                break;
        }

        // 面板展开动效
        float target = twinPanelOpen ? 1f : 0f;
        panelFade = Mathf.MoveTowards(panelFade, target, Time.deltaTime * 5f);
    }

    private void DrawGuide()
    {
        if (!loggedIn || guideStep > 4)
        {
            return;
        }

        string[] steps =
        {
            "① 按 T 打开「数字孪生监测平台」，查看现场传感器实时数据",
            "② 走到报警点位（场景中的数据牌），连点左键完成处置",
            "③ 改造后读数回落，保持正常 1 小时即通过闭环验收",
            "④ 在监测平台点「导出验收报告」，生成 KPI 对比报告",
            "⑤ 每完成 " + OrdersPerBuilding + " 单晋升一级并解锁一栋楼，从 1 号楼起步逐级扩张到 " + ResidenceCount + " 栋",
        };

        float width = 520f;
        Rect rect = new Rect(16f, Screen.height - 236f, width, 38f);
        DrawPanel(rect, new Color(0.05f, 0.09f, 0.13f, 0.94f), new Color(0.45f, 0.75f, 0.9f, 0.35f));
        Fill(new Rect(rect.x + 12f, rect.y + 9f, 4f, 20f), btnBlue);
        GUI.Label(new Rect(rect.x + 26f, rect.y + 9f, width - 40f, 22f), steps[Mathf.Clamp(guideStep, 0, 4)], smallStyle);
    }

    private void UpdateFade()
    {
        if (fadeAlpha > 0f)
        {
            fadeAlpha = Mathf.Max(0f, fadeAlpha - Time.deltaTime / 1.8f);
        }
    }

    private bool IsNight { get { return GameHour < 6 || GameHour >= 20; } }

    // 天黑后可以休息，直接跳到次日清晨 6:00
    private void RestUntilMorning()
    {
        if (GameHour >= 6 && GameHour < 20)
        {
            ShowToast("现在还是白天，先干活吧（天黑后按 R 休息）", 3f);
            return;
        }
        float hoursNow = gameTime / RealSecondsPerGameHour;
        float nextMorning = (Mathf.Floor(hoursNow / HoursPerDay) + 1f) * HoursPerDay + 6f;
        gameTime = nextMorning * RealSecondsPerGameHour;
        lastDay = DayIndex;
        lastPhaseMark = 0;
        StartTransition("第 " + GameMonth + " 月 第 " + DayOfMonth + " 天 · 清晨");
    }

    private void PaySalary()
    {
        AddIncome(MonthSalary);
        ShowToast("第 " + GameMonth + " 月工资到账 ¥" + MonthSalary.ToString("N0") + " —— 可以去服装店换身行头了", 9f);
    }

    private float GameHourFloat
    {
        get { return (gameTime / RealSecondsPerGameHour) % HoursPerDay; }
    }

    private int GameHour { get { return Mathf.FloorToInt(GameHourFloat); } }
    private int DayIndex { get { return Mathf.FloorToInt(gameTime / (RealSecondsPerGameHour * HoursPerDay)) + 1; } }
    private int GameMonth { get { return (DayIndex - 1) / MonthDays + 1; } }
    private int DayOfMonth { get { return (DayIndex - 1) % MonthDays + 1; } }

    private string ClockText
    {
        get { return "第 " + GameMonth + " 月 第 " + DayOfMonth + " 天  " + GameHour.ToString("D2") + ":" + Mathf.FloorToInt((GameHourFloat % 1f) * 60f).ToString("D2"); }
    }

    // 远处城市天际线：纯装饰，无碰撞
    private void BuildCitySkyline()
    {
        Color[] tones =
        {
            new Color(0.56f, 0.61f, 0.69f),
            new Color(0.47f, 0.53f, 0.62f),
            new Color(0.63f, 0.66f, 0.71f),
            new Color(0.41f, 0.47f, 0.56f),
            new Color(0.52f, 0.55f, 0.6f),
            new Color(0.36f, 0.42f, 0.52f),
        };
        // 所有城市窗户共用一个材质实例：合批后只需改它就能整片点亮
        cityWindowMaterial = MakeMaterial(cityWindowDayTone, 0f, 0.4f);
        Color windowTone = cityWindowDayTone;
        Color roofTone = new Color(0.34f, 0.37f, 0.42f);
        Color metalTone = new Color(0.6f, 0.63f, 0.67f);

        // 南面（马路那侧）的远景楼群，细节最丰富
        for (int i = 0; i < 16; i++)
        {
            float x = -95f + i * 13f + Random.Range(-3.5f, 3.5f);
            float z = -118f - Random.Range(0f, 35f);
            BuildCityTower(new Vector3(x, 0f, z), Random.Range(8f, 18f), Random.Range(8f, 13f),
                tones[Random.Range(0, tones.Length)], windowTone, roofTone, metalTone, true);
        }

        // 北面远景
        for (int i = 0; i < 13; i++)
        {
            float x = -80f + i * 14f + Random.Range(-3f, 3f);
            float z = 62f + Random.Range(0f, 24f);
            BuildCityTower(new Vector3(x, 0f, z), Random.Range(11f, 31f), Random.Range(8f, 14f),
                tones[Random.Range(0, tones.Length)], windowTone, roofTone, metalTone, false);
        }

        // 东西两侧远景
        for (int i = 0; i < 8; i++)
        {
            float z = -40f + i * 16f;
            BuildCityTower(new Vector3(-105f - Random.Range(0f, 22f), 0f, z), Random.Range(11f, 29f), 11f,
                tones[Random.Range(0, tones.Length)], windowTone, roofTone, metalTone, false);
            BuildCityTower(new Vector3(140f + Random.Range(0f, 22f), 0f, z), Random.Range(11f, 29f), 11f,
                tones[Random.Range(0, tones.Length)], windowTone, roofTone, metalTone, false);
        }
    }

    // 单栋远景楼：主体 + 退台 + 楼层窗带 + 屋顶设备 + 天线
    private void BuildCityTower(Vector3 basePosition, float height, float width, Color body, Color windowTone, Color roofTone, Color metalTone, bool detailed)
    {
        float depth = width * Random.Range(0.8f, 1.25f);
        CreateDecoCube("City Body", new Vector3(basePosition.x, height * 0.5f - 0.2f, basePosition.z),
            new Vector3(width, height, depth), body);

        float top = height - 0.2f;

        // 退台：高楼顶上加一层收进的体块
        if (height > 22f)
        {
            float upperH = Random.Range(4f, 9f);
            float upperW = width * Random.Range(0.5f, 0.72f);
            CreateDecoCube("City Upper", new Vector3(basePosition.x, top + upperH * 0.5f, basePosition.z),
                new Vector3(upperW, upperH, depth * Random.Range(0.55f, 0.78f)), body * 0.92f);
            top += upperH;
        }

        // 楼层窗带（面向街道一侧）
        int bands = Mathf.FloorToInt(height / 4.2f);
        for (int b = 1; b < bands; b++)
        {
            CreateDecoCube("City Windows", new Vector3(basePosition.x, b * 4.2f - 0.2f, basePosition.z - depth * 0.5f - 0.06f),
                new Vector3(width * 0.84f, 1.05f, 0.1f), cityWindowMaterial);
        }

        if (!detailed)
        {
            // 远景只加一条轮廓压顶
            CreateDecoCube("City Cap", new Vector3(basePosition.x, top + 0.2f, basePosition.z),
                new Vector3(width * 1.04f, 0.42f, depth * 1.04f), roofTone);
            return;
        }

        // 屋顶女儿墙 + 设备箱 + 天线，让轮廓不那么呆板
        CreateDecoCube("City Cap", new Vector3(basePosition.x, top + 0.25f, basePosition.z),
            new Vector3(width * 1.05f, 0.5f, depth * 1.05f), roofTone);

        int units = Random.Range(1, 4);
        for (int u = 0; u < units; u++)
        {
            float ox = Random.Range(-width * 0.3f, width * 0.3f);
            float oz = Random.Range(-depth * 0.3f, depth * 0.3f);
            float uh = Random.Range(0.6f, 1.8f);
            CreateDecoCube("City Roof Unit", new Vector3(basePosition.x + ox, top + 0.5f + uh * 0.5f, basePosition.z + oz),
                new Vector3(Random.Range(0.9f, 2.2f), uh, Random.Range(0.9f, 2.2f)), metalTone);
        }

        if (Random.value < 0.7f)
        {
            float mastH = Random.Range(2.5f, 7f);
            CreateDecoCube("City Mast", new Vector3(basePosition.x, top + 0.5f + mastH * 0.5f, basePosition.z),
                new Vector3(0.18f, mastH, 0.18f), metalTone);
        }
    }

    private void BuildTree(float x, float z)
    {
        Material trunk = MakeMaterial(new Color(0.37f, 0.26f, 0.16f), 0.02f, 0.3f);
        Material foliage = MakeMaterial(new Color(0.25f, 0.51f, 0.23f), 0.02f, 0.3f);
        CreateDecoCylinder("Tree Trunk", new Vector3(x, 1.1f, z), 0.15f, 2.2f, Quaternion.identity, trunk);
        CreateDecoSphere("Tree Crown 1", new Vector3(x, 2.9f, z), 1.25f, foliage);
        CreateDecoSphere("Tree Crown 2", new Vector3(x + 0.5f, 3.6f, z - 0.3f), 0.9f, foliage);
        CreateDecoSphere("Tree Crown 3", new Vector3(x - 0.55f, 3.4f, z + 0.35f), 0.8f, foliage);
    }

    private void BuildCloud(Vector3 center, float scale)
    {
        Material cloud = MakeMaterial(new Color(0.99f, 0.99f, 1f), 0f, 0.15f);
        CreateDecoSphere("Cloud", center, 3.0f * scale, cloud);
        CreateDecoSphere("Cloud", center + new Vector3(2.8f * scale, 0.4f * scale, 0.5f * scale), 2.1f * scale, cloud);
        CreateDecoSphere("Cloud", center + new Vector3(-2.6f * scale, 0.3f * scale, -0.4f * scale), 1.9f * scale, cloud);
        CreateDecoSphere("Cloud", center + new Vector3(0.4f * scale, 0.8f * scale, -1.4f * scale), 1.7f * scale, cloud);
    }

    private void BuildRoof(string name, float cx, float cz, float sizeX, float sizeZ, Color color)
    {
        GameObject roof = CreateDecoCube(name, new Vector3(cx, WallHeight + 0.22f, cz), new Vector3(sizeX + 0.9f, 0.44f, sizeZ + 0.9f), color);
        // 屋顶不投影，保证室内明亮
        Renderer renderer = roof.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        CreateDecoCube(name + " Trim", new Vector3(cx, WallHeight + 0.48f, cz), new Vector3(sizeX + 1.3f, 0.12f, sizeZ + 1.3f), new Color(0.42f, 0.42f, 0.44f));
    }

    // ── 装修公司 ──────────────────────────────────────────
    private void BuildCompany()
    {
        const float x0 = -20f, x1 = -8f, z0 = -7f, z1 = 5f;
        const float doorStart = -17.5f, doorEnd = -15.3f;   // 感应玻璃门
        Color wall = new Color(0.87f, 0.85f, 0.8f);

        CreateDecoCube("Company Floor", new Vector3(-14f, 0.01f, -1f), new Vector3(12f, 0.02f, 12f), new Color(0.74f, 0.72f, 0.68f));

        // 外墙：前墙留大门洞，左右与后墙开窗
        BuildWallWithOpenings("Company Wall Front", true, z0, x0, x1, 0.24f, wall,
            doorStart, doorEnd, 0f, 1.9f);
        BuildWallWithOpenings("Company Wall Back", true, z1, x0, x1, 0.24f, wall,
            -18f, -15.5f, 0.95f, 2.15f,
            -13f, -10.5f, 0.95f, 2.15f);
        BuildWallWithOpenings("Company Wall Left", false, x0, z0, z1, 0.24f, wall,
            -4.5f, -2f, 0.95f, 2.15f,
            1f, 3.5f, 0.95f, 2.15f);
        BuildWallWithOpenings("Company Wall Right", false, x1, z0, z1, 0.24f, wall,
            -3f, -0.5f, 0.95f, 2.15f);

        AddGlassPane(true, z1 - 0.14f, -18f, -15.5f, 0.95f, 2.15f);
        AddGlassPane(true, z1 - 0.14f, -13f, -10.5f, 0.95f, 2.15f);
        AddGlassPane(false, x0 + 0.14f, -4.5f, -2f, 0.95f, 2.15f);
        AddGlassPane(false, x0 + 0.14f, 1f, 3.5f, 0.95f, 2.15f);
        AddGlassPane(false, x1 - 0.14f, -3f, -0.5f, 0.95f, 2.15f);

        BuildRoof("Company Roof", -14f, -1f, 12f, 12f, new Color(0.42f, 0.36f, 0.34f));

        // 招牌 + 标价牌（前墙外侧）
        // 实测公式：每行世界高度 = characterSize × 64 ÷ 10，再乘 lineSpacing(1.1)，留足余量防外溢
        CreateDecoCube("Sign Board", new Vector3(-16.4f, 2.35f, z0 - 0.25f), new Vector3(5f, 0.78f, 0.12f), new Color(0.13f, 0.32f, 0.5f));
        CreateWorldLabel("焕新维修公司", new Vector3(-16.4f, 2.35f, z0 - 0.40f), 0.08f, Color.white);   // 3.07 × 0.56

        CreateDecoCube("Price Board", new Vector3(-12.6f, 1.45f, z0 - 0.25f), new Vector3(3.4f, 2.2f, 0.1f), new Color(0.93f, 0.92f, 0.88f));
        CreateDecoCube("Price Board Frame", new Vector3(-12.6f, 1.45f, z0 - 0.19f), new Vector3(3.7f, 2.5f, 0.08f), new Color(0.35f, 0.28f, 0.2f));
        CreateWorldLabel("维 修 价 目 表\n────────\n水路渗漏 ¥3200\n电路检修 ¥2600\n燃气管道 ¥4600\n墙面翻新 ¥2800",
            new Vector3(-12.6f, 1.45f, z0 - 0.40f), 0.040f, new Color(0.15f, 0.15f, 0.18f));           // 2.05 × 1.24

        // 室内陈设
        // 桌子 yaw 0：椅子在桌子北侧，人面朝南（正对大门）
        // 办公区往里挪，前台留在大门附近
        BuildDeskStation(-14.8f, -1.0f, 0f, false);
        BuildDeskStation(-12.0f, -1.0f, 0f, true);
        BuildCabinet(-19f, 3.4f, 0f);
        BuildPlant(-19.2f, -6f);
        BuildSofa(-9.6f, 2.6f, 270f);
        BuildPlant(-9.5f, -5.6f);

        // 接待台
        AddSolidBox("Reception", new Vector3(-13.2f, 0.5f, -5.6f), new Vector3(3.2f, 1f, 0.8f), new Color(0.55f, 0.38f, 0.24f));
        CreateDecoCube("Reception Top", new Vector3(-13.2f, 1.03f, -5.6f), new Vector3(3.4f, 0.08f, 0.95f), new Color(0.78f, 0.76f, 0.72f));

        // 吸顶灯
        BuildColleagues();
        BuildCeilingLight(-16f, -1f);
        BuildCeilingLight(-12f, -1f);
        BuildCeilingLight(-16f, 3f);
        BuildCeilingLight(-12f, 3f);
        AddRoomLight(-14f, -1f, 18f);

        rooms.Add(new Room { name = "装修公司", type = "公司", building = 0, center = new Vector3(-14f, 0f, -1f), xMin = x0, xMax = x1, zMin = z0, zMax = z1 });

        BuildSensorDoor(doorStart, doorEnd, z0);
    }

    // 感应式透明玻璃门：玩家靠近自动打开
    private void BuildSensorDoor(float start, float end, float z)
    {
        Material frame = MakeMaterial(new Color(0.42f, 0.44f, 0.47f), 0.7f, 0.7f);
        Material glass = MakeTransparent(new Color(0.94f, 0.96f, 0.97f), 0.12f, 0.95f);
        float center = (start + end) * 0.5f;
        float width = (end - start) * 0.5f - 0.05f;

        GameObject left = new GameObject("Sensor Door Left");
        left.transform.SetParent(transform, false);
        left.transform.position = new Vector3(center, 0f, z);
        GameObject right = new GameObject("Sensor Door Right");
        right.transform.SetParent(transform, false);
        right.transform.position = new Vector3(center, 0f, z);

        DecoPart(PrimitiveType.Cube, "Glass", left.transform, new Vector3(-width * 0.5f, 1.15f, 0f), new Vector3(width, 2.2f, 0.06f), Quaternion.identity, glass);
        DecoPart(PrimitiveType.Cube, "Frame", left.transform, new Vector3(-width * 0.5f, 2.28f, 0f), new Vector3(width, 0.1f, 0.09f), Quaternion.identity, frame);
        DecoPart(PrimitiveType.Cube, "Frame", left.transform, new Vector3(-width, 1.15f, 0f), new Vector3(0.07f, 2.3f, 0.09f), Quaternion.identity, frame);

        DecoPart(PrimitiveType.Cube, "Glass", right.transform, new Vector3(width * 0.5f, 1.15f, 0f), new Vector3(width, 2.2f, 0.06f), Quaternion.identity, glass);
        DecoPart(PrimitiveType.Cube, "Frame", right.transform, new Vector3(width * 0.5f, 2.28f, 0f), new Vector3(width, 0.1f, 0.09f), Quaternion.identity, frame);
        DecoPart(PrimitiveType.Cube, "Frame", right.transform, new Vector3(width, 1.15f, 0f), new Vector3(0.07f, 2.3f, 0.09f), Quaternion.identity, frame);

        sensorDoorLeft = left.transform;
        sensorDoorRight = right.transform;
        sensorDoorCenter = new Vector3(center, 0f, z);
        sensorDoorHalf = width;
    }

    // 吸顶灯具外观（每个房间一盏）
    private void BuildCeilingLight(float x, float z)
    {
        CreateDecoCube("Ceiling Panel", new Vector3(x, WallHeight - 0.08f, z), new Vector3(0.7f, 0.06f, 0.7f), new Color(0.96f, 0.94f, 0.86f));
    }

    // 实际点光源：每栋楼只放一盏大范围灯，避免 WebGL 前向渲染下光源过多掉帧
    private void AddRoomLight(float x, float z, float range)
    {
        GameObject lightObject = new GameObject("Room Light");
        lightObject.transform.SetParent(transform, false);
        lightObject.transform.position = new Vector3(x, WallHeight - 0.4f, z);
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = range * 1.3f;
        light.intensity = 1.7f;
        light.color = new Color(1f, 0.94f, 0.85f);
        light.enabled = false;   // 白天不开灯
        roomLights.Add(light);
        generatedObjects.Add(lightObject);
    }

    private void CreateWorldLabel(string text, Vector3 position, float size, Color color)
    {
        GameObject label = new GameObject("Label");
        label.transform.SetParent(transform, false);
        label.transform.position = position;
        // TextMesh 默认正面朝 -Z，正好面向从南侧道路走来的玩家
        label.transform.rotation = Quaternion.identity;

        TextMesh mesh = label.AddComponent<TextMesh>();
        mesh.font = UiFont;
        mesh.text = text;
        mesh.fontSize = 64;
        mesh.characterSize = size;
        mesh.anchor = TextAnchor.MiddleCenter;
        mesh.alignment = TextAlignment.Center;
        mesh.color = color;
        mesh.lineSpacing = 1.1f;

        Renderer renderer = label.GetComponent<Renderer>();
        if (mesh.font != null)
        {
            // 直接用 Alpha 裁切材质（写深度、剔除背面），避免文字穿墙从背面可见
            // （不能依赖 ApplyLabelMaterials：员工宿舍等标签在它之后才构建）
            renderer.sharedMaterial = GetLabelMaterial(color);
        }
        pendingLabels.Add(new LabelEntry { renderer = renderer, color = color });
        generatedObjects.Add(label);
    }

    private class LabelEntry
    {
        public Renderer renderer;
        public Color color;
    }

    private readonly List<LabelEntry> pendingLabels = new List<LabelEntry>();
    private static readonly Dictionary<Color, Material> labelMaterials = new Dictionary<Color, Material>();

    // 所有文字建完后统一替换材质：字体自带的材质是双面且不写深度的，会导致穿墙显示与镜像字
    private void ApplyLabelMaterials()
    {
        for (int i = 0; i < pendingLabels.Count; i++)
        {
            LabelEntry entry = pendingLabels[i];
            if (entry.renderer != null)
            {
                entry.renderer.sharedMaterial = GetLabelMaterial(entry.color);
            }
        }
    }

    private Material GetLabelMaterial(Color color)
    {
        Material material;
        if (labelMaterials.TryGetValue(color, out material) && material != null)
        {
            return material;
        }

        Font font = UiFont;
        Texture atlas = font != null && font.material != null ? font.material.mainTexture : null;

        // 优先用 Alpha 裁切的单面着色器：写深度、剔除背面，文字不会穿墙也不会露镜像
        // 用「无光照」的 Alpha 裁切着色器：文字自身不参与光照，贴在板上不会产生投影/明暗
        Shader shader = Shader.Find("Unlit/Transparent Cutout");
        bool standard = false;
        if (shader == null)
        {
            shader = Shader.Find("Transparent/Cutout/Diffuse");
        }
        if (shader == null)
        {
            shader = Shader.Find("Standard");
            standard = true;
        }
        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
        }

        material = new Material(shader);
        if (atlas != null)
        {
            material.mainTexture = atlas;
        }
        material.color = color;
        if (material.HasProperty("_Cutoff"))
        {
            material.SetFloat("_Cutoff", 0.3f);
        }
        if (standard)
        {
            material.SetFloat("_Mode", 1f);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            material.SetInt("_ZWrite", 1);
            material.EnableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHABLEND_ON");
            material.renderQueue = 2450;
            material.SetFloat("_Glossiness", 0f);
            material.SetFloat("_Metallic", 0f);
        }
        labelMaterials[color] = material;
        return material;
    }

    private void BuildDeskStation(float x, float z, float yaw, bool withChair)
    {
        Transform g = CreateGroup("Desk Station", new Vector3(x, 0f, z), yaw);
        Material desk = MakeMaterial(new Color(0.7f, 0.57f, 0.42f), 0.02f, 0.4f);
        Material leg = MakeMaterial(new Color(0.42f, 0.43f, 0.45f), 0.4f, 0.5f);
        Material screen = MakeMaterial(new Color(0.09f, 0.1f, 0.12f), 0.3f, 0.7f);

        DecoPart(PrimitiveType.Cube, "Desk Top", g, new Vector3(0f, 0.74f, 0f), new Vector3(1.9f, 0.08f, 0.95f), Quaternion.identity, desk);
        for (int i = 0; i < 4; i++)
        {
            float ox = (i % 2 == 0) ? -0.85f : 0.85f;
            float oz = (i < 2) ? -0.38f : 0.38f;
            DecoPart(PrimitiveType.Cube, "Leg", g, new Vector3(ox, 0.37f, oz), new Vector3(0.07f, 0.74f, 0.07f), Quaternion.identity, leg);
        }
        DecoPart(PrimitiveType.Cube, "Monitor", g, new Vector3(0f, 1.02f, -0.22f), new Vector3(0.72f, 0.46f, 0.05f), Quaternion.identity, screen);
        DecoPart(PrimitiveType.Cube, "Monitor Stand", g, new Vector3(0f, 0.82f, -0.22f), new Vector3(0.08f, 0.14f, 0.08f), Quaternion.identity, screen);
        DecoPart(PrimitiveType.Cube, "Keyboard", g, new Vector3(0f, 0.8f, 0.12f), new Vector3(0.5f, 0.03f, 0.18f), Quaternion.identity, screen);
        AddRotatedObstacle("Desk Body", new Vector3(x, 0.45f, z), new Vector3(2f, 0.9f, 1.05f), yaw);

        // 转椅（玩家自己的工位不摆椅子，避免和出生点重叠）
        if (withChair)
        {
            Vector3 chairWorld = new Vector3(x, 0f, z) + Quaternion.Euler(0f, yaw, 0f) * new Vector3(0f, 0f, 0.95f);
            BuildChair(chairWorld.x, chairWorld.z, yaw + 180f);
        }
    }

    // ── 住宅（4 栋单层，每栋 4 个房间）────────────────────
    private void BuildHouse()
    {
        Color[] roofColors =
        {
            new Color(0.55f, 0.33f, 0.27f),
            new Color(0.36f, 0.44f, 0.42f),
            new Color(0.5f, 0.4f, 0.28f),
            new Color(0.42f, 0.38f, 0.48f),
            new Color(0.5f, 0.46f, 0.36f),
            new Color(0.34f, 0.4f, 0.5f),
            new Color(0.48f, 0.34f, 0.4f),
        };
        // 第一排（临街）
        BuildResidence(1, 2f, -7f, roofColors[0]);
        BuildResidence(2, 18f, -7f, roofColors[1]);
        BuildResidence(3, 34f, -7f, roofColors[2]);
        // 第二排（错开半格，形成小区内街）
        BuildResidence(4, 10f, 13f, roofColors[4]);
        BuildResidence(5, 26f, 13f, roofColors[5]);
    }

    // 单层住宅：12×12，四个 6×6 房间
    //   前左 客厅（入户）/ 前右 厨房
    //   后左 卫生间       / 后右 卧室
    // ── 户型参数：参考现实住宅，每家开间/进深/隔墙位置/房间组合都不同 ──
    private class HouseLayout
    {
        public float width;      // 开间
        public float depth;      // 进深
        public float splitX;     // 前排竖向隔墙（距左侧外墙）
        public float splitX2;    // 后排竖向隔墙（可与前排不同，用于让卫生间更小）
        public float splitZ;     // 横向隔墙（距前墙）
        public string frontRight;
        public string backLeft;
        public string backRight;

        public HouseLayout(float width, float depth, float splitX, float splitX2, float splitZ,
            string frontRight, string backLeft, string backRight)
        {
            this.width = width;
            this.depth = depth;
            this.splitX = splitX;
            this.splitX2 = splitX2;
            this.splitZ = splitZ;
            this.frontRight = frontRight;
            this.backLeft = backLeft;
            this.backRight = backRight;
        }
    }

    // 前左固定为客厅（入户所在）；厨房、卫生间靠近入口，卧室靠里
    private static readonly HouseLayout[] HouseLayouts =
    {
        new HouseLayout(12f, 12f, 8.6f, 6.0f, 6.0f, "卫生间", "厨房", "卧室"),
        new HouseLayout(11f, 13f, 8.0f, 3.2f, 7.0f, "厨房", "卫生间", "卧室"),
        new HouseLayout(13f, 11f, 9.4f, 6.6f, 5.4f, "卫生间", "厨房", "卧室"),
        new HouseLayout(12f, 11f, 8.4f, 5.6f, 5.6f, "卫生间", "厨房", "卧室"),
        new HouseLayout(10f, 12f, 6.8f, 5.0f, 7.0f, "卫生间", "厨房", "卧室"),
        new HouseLayout(13f, 12f, 9.6f, 3.4f, 6.4f, "厨房", "卫生间", "卧室"),
        new HouseLayout(11f, 11f, 7.8f, 5.4f, 5.8f, "卫生间", "厨房", "卧室"),
    };

    private static Color FloorColorOf(string type)
    {
        switch (type)
        {
            case "厨房": return new Color(0.76f, 0.78f, 0.76f);
            case "卫生间": return new Color(0.80f, 0.85f, 0.86f);
            case "卧室": return new Color(0.85f, 0.76f, 0.62f);
            default: return new Color(0.82f, 0.72f, 0.58f);
        }
    }

    private static bool FloorIsWood(string type)
    {
        return type == "客厅" || type == "卧室";
    }

    // 在 [a,b] 段中部取一段窗洞，两端各留 margin
    private static void WindowRange(float a, float b, float margin, float maxWidth, out float ws, out float we)
    {
        float mid = (a + b) * 0.5f;
        float w = Mathf.Clamp((b - a) - margin * 2f, 0.8f, maxWidth);
        ws = mid - w * 0.5f;
        we = mid + w * 0.5f;
    }

    private void BuildResidence(int index, float x0, float z0, Color roofColor)
    {
        HouseLayout L = HouseLayouts[(index - 1) % HouseLayouts.Length];
        float x1 = x0 + L.splitX;         // 前排竖向隔墙
        float x3 = x0 + L.splitX2;        // 后排竖向隔墙（让卫生间更窄）
        float x2 = x0 + L.width;
        float zMid = z0 + L.splitZ;
        float z1 = z0 + L.depth;
        Color wall = new Color(0.88f, 0.86f, 0.81f);
        Color inner = new Color(0.84f, 0.82f, 0.77f);
        string tag = index + "号楼";

        string[] types = { "客厅", L.frontRight, L.backLeft, L.backRight };
        float[,] rects =
        {
            { x0, x1, z0, zMid },
            { x1, x2, z0, zMid },
            { x0, x3, zMid, z1 },
            { x3, x2, zMid, z1 },
        };

        for (int i = 0; i < 4; i++)
        {
            BuildRoomFloor(rects[i, 0], rects[i, 1], rects[i, 2], rects[i, 3], FloorColorOf(types[i]), FloorIsWood(types[i]));
        }

        // ── 外墙：入户门 + 按房间外墙面自动开窗 ──
        float doorW = Mathf.Min(2.2f, L.splitX - 1.2f);
        float doorC = (x0 + x1) * 0.5f;
        float ws, we;
        List<float> front = new List<float> { doorC - doorW * 0.5f, doorC + doorW * 0.5f, 0f, DoorHeight };
        if (x2 - x1 > 3.0f)
        {
            WindowRange(x1, x2, 0.9f, 2.4f, out ws, out we);
            front.AddRange(new[] { ws, we, 0.95f, 2.15f });
            AddGlassPane(true, z0 + 0.14f, ws, we, 0.95f, 2.15f);
        }
        BuildWallWithOpenings("Res Front Wall", true, z0, x0, x2, 0.24f, wall, front.ToArray());

        List<float> back = new List<float>();
        WindowRange(x0, x1, 0.9f, 2.6f, out ws, out we);
        back.AddRange(new[] { ws, we, 0.95f, 2.15f });
        AddGlassPane(true, z1 - 0.14f, ws, we, 0.95f, 2.15f);
        if (x2 - x1 > 3.0f)
        {
            WindowRange(x1, x2, 0.9f, 2.6f, out ws, out we);
            back.AddRange(new[] { ws, we, 0.95f, 2.15f });
            AddGlassPane(true, z1 - 0.14f, ws, we, 0.95f, 2.15f);
        }
        BuildWallWithOpenings("Res Back Wall", true, z1, x0, x2, 0.24f, wall, back.ToArray());

        List<float> left = new List<float>();
        WindowRange(z0, zMid, 0.9f, 2.4f, out ws, out we);
        left.AddRange(new[] { ws, we, 0.95f, 2.15f });
        AddGlassPane(false, x0 + 0.14f, ws, we, 0.95f, 2.15f);
        WindowRange(zMid, z1, 0.9f, 2.4f, out ws, out we);
        left.AddRange(new[] { ws, we, 0.95f, 2.15f });
        AddGlassPane(false, x0 + 0.14f, ws, we, 0.95f, 2.15f);
        BuildWallWithOpenings("Res Left Wall", false, x0, z0, z1, 0.24f, wall, left.ToArray());

        List<float> right = new List<float>();
        WindowRange(z0, zMid, 0.9f, 2.4f, out ws, out we);
        right.AddRange(new[] { ws, we, 0.95f, 2.15f });
        AddGlassPane(false, x2 - 0.14f, ws, we, 0.95f, 2.15f);
        if (z1 - zMid > 3.0f)
        {
            WindowRange(zMid, z1, 0.9f, 2.4f, out ws, out we);
            right.AddRange(new[] { ws, we, 0.95f, 2.15f });
            AddGlassPane(false, x2 - 0.14f, ws, we, 0.95f, 2.15f);
        }
        BuildWallWithOpenings("Res Right Wall", false, x2, z0, z1, 0.24f, wall, right.ToArray());

        // ── 内墙：门洞开在相邻两房间正中，随隔墙位置自动变化 ──
        float dDoor = Mathf.Min(1.6f, (zMid - z0) * 0.28f);
        BuildWallWithOpenings("Res Wall Front Vertical", false, x1, z0, zMid, 0.22f, inner,
            (z0 + zMid) * 0.5f - dDoor, (z0 + zMid) * 0.5f + dDoor, 0f, DoorHeight);
        BuildWallWithOpenings("Res Wall Back Vertical", false, x3, zMid, z1, 0.22f, inner,
            (zMid + z1) * 0.5f - dDoor, (zMid + z1) * 0.5f + dDoor, 0f, DoorHeight);

        float leftOverlap1 = Mathf.Min(x1, x3);
        float rightOverlap0 = Mathf.Max(x1, x3);
        float hDoor = Mathf.Min(1.6f, (x2 - x0) * 0.14f);
        float dl = (x0 + leftOverlap1) * 0.5f;
        float dr = (rightOverlap0 + x2) * 0.5f;
        BuildWallWithOpenings("Res Wall Horizontal", true, zMid, x0, x2, 0.22f, inner,
            dl - hDoor, dl + hDoor, 0f, DoorHeight,
            dr - hDoor, dr + hDoor, 0f, DoorHeight);

        BuildRoof(tag + " Roof", x0 + L.width * 0.5f, z0 + L.depth * 0.5f, L.width, L.depth, roofColor);

        Vector3 doorPoint = new Vector3(doorC, GroundLevel, z0 + 1.6f);
        for (int i = 0; i < 4; i++)
        {
            AddRoom(tag + " " + types[i], types[i], index, rects[i, 0], rects[i, 1], rects[i, 2], rects[i, 3], doorPoint);
        }

        for (int i = 0; i < 4; i++)
        {
            float cx = (rects[i, 0] + rects[i, 1]) * 0.5f;
            float cz = (rects[i, 2] + rects[i, 3]) * 0.5f;
            float rw = rects[i, 1] - rects[i, 0];
            float rd = rects[i, 3] - rects[i, 2];
            switch (types[i])
            {
                case "厨房": BuildKitchenRoom(cx, cz, rw, rd); break;
                case "卫生间": BuildBathroom(cx, cz, rw, rd); break;
                case "卧室": BuildBedroom(cx, cz, rw, rd); break;
                default: BuildLivingRoom(cx, cz, rw, rd); break;
            }
        }

        BuildCeilingLight((x0 + x1) * 0.5f, (z0 + zMid) * 0.5f);
        BuildCeilingLight((x1 + x2) * 0.5f, (z0 + zMid) * 0.5f);
        BuildCeilingLight((x0 + x1) * 0.5f, (zMid + z1) * 0.5f);
        BuildCeilingLight((x1 + x2) * 0.5f, (zMid + z1) * 0.5f);
        AddRoomLight(x0 + L.width * 0.5f, z0 + L.depth * 0.5f, Mathf.Max(L.width, L.depth) * 1.4f);

        BuildHouseDoor(tag + " Door", doorC - doorW * 0.5f, doorC + doorW * 0.5f, z0);

        CreateDecoCube(tag + " Plate", new Vector3(x2 - 1.6f, 1.75f, z0 - 0.19f), new Vector3(1.5f, 0.6f, 0.08f), new Color(0.16f, 0.24f, 0.4f));
        CreateWorldLabel(index + "号楼", new Vector3(x2 - 1.6f, 1.75f, z0 - 0.32f), 0.05f, Color.white);
    }

    private void AddRoom(string name, string type, int building, float xMin, float xMax, float zMin, float zMax, Vector3 doorPoint)
    {
        rooms.Add(new Room
        {
            name = name,
            type = type,
            building = building,
            center = new Vector3((xMin + xMax) * 0.5f, 0f, (zMin + zMax) * 0.5f),
            doorPoint = doorPoint,
            xMin = xMin,
            xMax = xMax,
            zMin = zMin,
            zMax = zMax
        });
    }

    // 可开关的房门：按 F 切换，门扇绕合页旋转
    private void BuildHouseDoor(string name, float start, float end, float z)
    {
        Material wood = MakeMaterial(new Color(0.6f, 0.42f, 0.24f), 0.02f, 0.4f);
        Material handle = MakeMaterial(new Color(0.82f, 0.78f, 0.45f), 0.7f, 0.6f);

        GameObject hinge = new GameObject(name);
        hinge.transform.SetParent(transform, false);
        hinge.transform.position = new Vector3(start, 0f, z);

        float width = end - start;
        DecoPart(PrimitiveType.Cube, "Door Panel", hinge.transform, new Vector3(width * 0.5f, 1.05f, 0f), new Vector3(width - 0.06f, 2.06f, 0.07f), Quaternion.identity, wood);
        DecoPart(PrimitiveType.Cylinder, "Knob", hinge.transform, new Vector3(width - 0.2f, 1.05f, -0.07f), new Vector3(0.035f, 0.03f, 0.035f), Quaternion.Euler(90f, 0f, 0f), handle);

        houseDoors.Add(new HouseDoor { pivot = hinge.transform, open = 0f, target = 0f, xMin = start, xMax = end, z = z });
    }

    // ── 门动画 ────────────────────────────────────────────
    private void UpdateDoors()
    {
        // 住宅房门：按 F 开关，门扇绕合页旋转
        if (Input.GetKeyDown(KeyCode.F) && dialogueIndex < 0 && repairingOrder == null)
        {
            HouseDoor nearest = null;
            float best = 3.2f;
            for (int i = 0; i < houseDoors.Count; i++)
            {
                float d = Distance2D(playerPosition, houseDoors[i].pivot.position);
                if (d < best)
                {
                    best = d;
                    nearest = houseDoors[i];
                }
            }
            if (nearest != null)
            {
                nearest.manualOpen = !nearest.manualOpen;
                ShowToast(nearest.manualOpen ? "门已设为常开" : "门已设为常闭（有人靠近仍会自动开）", 2.5f);
            }
        }

        for (int i = 0; i < houseDoors.Count; i++)
        {
            HouseDoor door = houseDoors[i];
            if (door.pivot == null)
            {
                continue;
            }

            // 有人（玩家或 NPC）靠近就自动开门，否则回到玩家手动设定的状态
            Vector3 doorMid = new Vector3((door.xMin + door.xMax) * 0.5f, 0f, door.z);
            bool someone = Distance2D(playerPosition, doorMid) < 2.7f || AnyoneNear(doorMid, 2.7f);
            door.target = someone ? 1f : (door.manualOpen ? 1f : 0f);

            door.open = Mathf.Lerp(door.open, door.target, Time.deltaTime * 5f);
            door.pivot.localRotation = Quaternion.Euler(0f, -95f * door.open, 0f);
        }

        // 公司感应玻璃门：玩家或任何 NPC 靠近都自动滑开
        if (sensorDoorLeft != null && sensorDoorRight != null)
        {
            bool anyone = Distance2D(playerPosition, sensorDoorCenter) < 3.4f
                || AnyoneNear(sensorDoorCenter, 3.4f);
            float target = anyone ? 1f : 0f;
            sensorDoorOpen = Mathf.Lerp(sensorDoorOpen, target, Time.deltaTime * 4f);
            float slide = sensorDoorHalf * sensorDoorOpen;
            sensorDoorLeft.position = new Vector3(sensorDoorCenter.x - slide, 0f, sensorDoorCenter.z);
            sensorDoorRight.position = new Vector3(sensorDoorCenter.x + slide, 0f, sensorDoorCenter.z);
        }
    }

    // 各房间家具：坐标以房间中心为基准（房间 6×6）
    private void BuildKitchenRoom(float cx, float cz)
    {
        Material cab = MakeMaterial(new Color(0.5f, 0.36f, 0.22f), 0.02f, 0.4f);
        Material door = MakeMaterial(new Color(0.62f, 0.46f, 0.3f), 0.02f, 0.4f);
        EnsureTextures();
        Material top = TexturedMaterial(new Color(0.74f, 0.73f, 0.7f), stoneTexture, 3f);
        Material steel = MakeMaterial(new Color(0.72f, 0.75f, 0.78f), 0.75f, 0.7f);
        float counterZ = cz + 2.3f;

        for (int i = 0; i < 3; i++)
        {
            float x = cx - 2f + i * 2f;
            DecoPart(PrimitiveType.Cube, "Cabinet", transform, new Vector3(x, 0.45f, counterZ), new Vector3(1.9f, 0.9f, 1.1f), Quaternion.identity, cab);
            DecoPart(PrimitiveType.Cube, "Door", transform, new Vector3(x, 0.45f, counterZ - 0.58f), new Vector3(1.6f, 0.74f, 0.05f), Quaternion.identity, door);
            DecoPart(PrimitiveType.Cylinder, "Handle", transform, new Vector3(x + 0.55f, 0.45f, counterZ - 0.62f), new Vector3(0.022f, 0.16f, 0.022f), Quaternion.Euler(90f, 0f, 0f), steel);
            DecoPart(PrimitiveType.Cube, "Wall Cabinet", transform, new Vector3(x, 1.9f, counterZ + 0.3f), new Vector3(1.8f, 0.7f, 0.45f), Quaternion.identity, cab);
            DecoPart(PrimitiveType.Cube, "Wall Cabinet Door", transform, new Vector3(x, 1.9f, counterZ + 0.06f), new Vector3(1.6f, 0.56f, 0.04f), Quaternion.identity, door);
        }
        AddRotatedObstacle("Kitchen Counter", new Vector3(cx, 0.45f, counterZ), new Vector3(6f, 0.9f, 1.15f), 0f);
        DecoPart(PrimitiveType.Cube, "Countertop", transform, new Vector3(cx, 0.93f, counterZ), new Vector3(6.2f, 0.07f, 1.25f), Quaternion.identity, top);

        DecoPart(PrimitiveType.Cube, "Sink", transform, new Vector3(cx - 1.5f, 0.98f, counterZ), new Vector3(1.1f, 0.05f, 0.7f), Quaternion.identity, steel);
        DecoPart(PrimitiveType.Cylinder, "Faucet", transform, new Vector3(cx - 1.5f, 1.14f, counterZ + 0.26f), new Vector3(0.03f, 0.32f, 0.03f), Quaternion.identity, steel);
        DecoPart(PrimitiveType.Cube, "Stove", transform, new Vector3(cx + 1.4f, 0.98f, counterZ), new Vector3(1.2f, 0.05f, 0.7f), Quaternion.identity, MakeMaterial(new Color(0.12f, 0.13f, 0.15f), 0.2f, 0.5f));
        DecoPart(PrimitiveType.Cylinder, "Pot", transform, new Vector3(cx + 1.4f, 1.06f, counterZ), new Vector3(0.15f, 0.12f, 0.15f), Quaternion.identity, steel);

        AddSolidBox("Fridge", new Vector3(cx + 2.3f, 0.9f, cz - 1.6f), new Vector3(0.85f, 1.8f, 0.85f), new Color(0.78f, 0.8f, 0.82f));
        DecoPart(PrimitiveType.Cylinder, "Fridge Handle", transform, new Vector3(cx + 2.3f, 1.4f, cz - 2.05f), new Vector3(0.02f, 0.3f, 0.02f), Quaternion.identity, steel);
    }

    private void BuildBathroom(float cx, float cz)
    {
        // 洁具靠墙布置：马桶靠东墙、洗手台靠西墙、浴缸沿北墙
        BuildToilet(cx + 2.45f, cz - 0.9f, 270f);
        BuildWashbasin(cx - 2.55f, cz - 0.6f, 90f);
        BuildBathtub(cx - 0.3f, cz + 2.3f, 0f);
    }

    private void BuildBedroom(float cx, float cz)
    {
        // 床头靠北墙（南侧是通往客厅的门，避免挡门）
        BuildBed(cx - 0.5f, cz + 1.85f, 180f);
        BuildCabinet(cx + 1.1f, cz + 2.45f, 180f);   // 床头柜贴床头东侧
        BuildWardrobe(cx - 2.6f, cz - 0.8f, 90f);    // 衣柜靠西墙，面朝东
        BuildPlant(cx + 2.2f, cz - 2.2f);
    }

    // 家具布置：全部按房间实际尺寸计算，适配不同户型
    private void BuildLivingRoom(float cx, float cz, float w, float d)
    {
        float left = cx - w * 0.5f, right = cx + w * 0.5f;
        float front = cz - d * 0.5f, back = cz + d * 0.5f;

        // 坐具轴线：需在入户门扇的扫掠区之外（门向内摆，扫掠半径约等于门宽 2.2m）
        float axisZ = Mathf.Min(front + 3.7f, back - 1.15f);
        float tvX = left + 0.45f;
        float sofaX = right - 0.65f;

        BuildTvUnit(tvX, axisZ, 90f);                       // 电视靠西墙，面朝东
        BuildSofa(sofaX, axisZ, 270f);                      // 沙发正对电视，面朝西
        BuildTable(cx, axisZ, 0.68f, Mathf.Min(1.35f, w * 0.42f), 0f, 0.45f);   // 长边沿 Z，与沙发视线垂直
        CreateDecoCube("Rug", new Vector3(cx, 0.02f, axisZ),
            new Vector3(Mathf.Max(1.6f, w - 1.5f), 0.02f, 2.0f), new Color(0.68f, 0.55f, 0.44f));
        BuildPlant(left + 0.55f, back - 0.65f);             // 绿植摆后角，不挡门

        // 装饰：电视墙挂画、茶几摆件、吊灯、窗帘
        BuildWallArt(tvX + 0.14f, axisZ + 1.35f, new Color(0.62f, 0.48f, 0.4f));
        BuildWallArt(tvX + 0.14f, axisZ - 1.35f, new Color(0.44f, 0.52f, 0.48f));
        BuildTableDecor(cx, axisZ);
        BuildPendantLamp(cx, axisZ);
        BuildCurtains();
    }

    // 电视墙挂画（画框 + 画心）
    private void BuildWallArt(float wallX, float z, Color artColor)
    {
        Material frame = MakeMaterial(new Color(0.28f, 0.22f, 0.17f), 0.02f, 0.4f);
        Material art = MakeMaterial(artColor, 0.02f, 0.3f);
        CreateDecoCube("Art Frame", new Vector3(wallX, 1.72f, z), new Vector3(0.06f, 0.72f, 0.95f), frame);
        CreateDecoCube("Art", new Vector3(wallX + 0.05f, 1.72f, z), new Vector3(0.03f, 0.58f, 0.8f), art);
    }

    // 茶几摆件：托盘 + 果盘 + 花瓶
    private void BuildTableDecor(float cx, float cz)
    {
        Material tray = MakeMaterial(new Color(0.35f, 0.3f, 0.26f), 0.05f, 0.4f);
        Material bowl = MakeMaterial(new Color(0.86f, 0.84f, 0.78f), 0.05f, 0.5f);
        Material vase = MakeMaterial(new Color(0.55f, 0.62f, 0.6f), 0.05f, 0.5f);
        CreateDecoCube("Tray", new Vector3(cx + 0.02f, 0.5f, cz), new Vector3(0.34f, 0.025f, 0.44f), tray);
        CreateDecoCylinder("Fruit Bowl", new Vector3(cx + 0.02f, 0.55f, cz + 0.12f), 0.1f, 0.07f, Quaternion.identity, bowl);
        CreateDecoCylinder("Vase", new Vector3(cx + 0.02f, 0.6f, cz - 0.42f), 0.055f, 0.2f, Quaternion.identity, vase);
        CreateDecoSphere("Flower", new Vector3(cx + 0.02f, 0.76f, cz - 0.42f), 0.07f, MakeMaterial(new Color(0.78f, 0.42f, 0.45f), 0.02f, 0.35f));
    }

    // 客厅吊灯
    private void BuildPendantLamp(float cx, float cz)
    {
        Material shell = MakeMaterial(new Color(0.92f, 0.9f, 0.84f), 0.05f, 0.5f);
        Material cord = MakeMaterial(new Color(0.18f, 0.18f, 0.2f), 0.05f, 0.4f);
        CreateDecoCylinder("Lamp Cord", new Vector3(cx, 2.3f, cz), 0.008f, 0.7f, Quaternion.identity, cord);
        CreateDecoCylinder("Lamp Shade", new Vector3(cx, 1.95f, cz), 0.23f, 0.17f, Quaternion.identity, shell);
        CreateDecoSphere("Lamp Bulb", new Vector3(cx, 1.88f, cz), 0.085f, MakeGlow(new Color(1f, 0.94f, 0.78f), 1.2f));
    }

    // 窗帘（挂在客厅东西两侧窗洞旁）
    private void BuildCurtains()
    {
        Material cloth = MakeMaterial(new Color(0.72f, 0.66f, 0.6f), 0.02f, 0.35f);
        CreateDecoCube("Curtain W", new Vector3(-27.6f, 1.55f, -1.6f), new Vector3(0.08f, 1.3f, 0.5f), cloth);
        CreateDecoCube("Curtain E", new Vector3(-23.4f, 1.55f, -1.6f), new Vector3(0.08f, 1.3f, 0.5f), cloth);
    }

    private void BuildKitchenRoom(float cx, float cz, float w, float d)
    {
        float left = cx - w * 0.5f, right = cx + w * 0.5f;
        float back = cz + d * 0.5f;
        float counterZ = back - 0.72f;

        Material cab = MakeMaterial(new Color(0.5f, 0.36f, 0.22f), 0.02f, 0.4f);
        Material door = MakeMaterial(new Color(0.62f, 0.46f, 0.3f), 0.02f, 0.4f);
        Material steel = MakeMaterial(new Color(0.72f, 0.75f, 0.78f), 0.75f, 0.7f);
        EnsureTextures();
        Material top = TexturedMaterial(new Color(0.74f, 0.73f, 0.7f), stoneTexture, 3f);

        // 柜体沿后墙铺满可用宽度，段数随房宽变化
        float usable = w - 0.7f;
        int count = Mathf.Max(1, Mathf.RoundToInt(usable / 1.9f));
        float seg = usable / count;
        for (int i = 0; i < count; i++)
        {
            float x = left + 0.35f + seg * (i + 0.5f);
            DecoPart(PrimitiveType.Cube, "Cabinet", transform, new Vector3(x, 0.45f, counterZ), new Vector3(seg - 0.06f, 0.9f, 1.06f), Quaternion.identity, cab);
            DecoPart(PrimitiveType.Cube, "Door", transform, new Vector3(x, 0.45f, counterZ - 0.56f), new Vector3(seg - 0.3f, 0.74f, 0.05f), Quaternion.identity, door);
            DecoPart(PrimitiveType.Cylinder, "Handle", transform, new Vector3(x + seg * 0.28f, 0.45f, counterZ - 0.6f), new Vector3(0.022f, 0.16f, 0.022f), Quaternion.Euler(90f, 0f, 0f), steel);
            DecoPart(PrimitiveType.Cube, "Wall Cabinet", transform, new Vector3(x, 1.9f, counterZ + 0.32f), new Vector3(seg - 0.14f, 0.7f, 0.42f), Quaternion.identity, cab);
        }
        AddRotatedObstacle("Kitchen Counter", new Vector3(cx, 0.45f, counterZ), new Vector3(usable, 0.9f, 1.1f), 0f);
        DecoPart(PrimitiveType.Cube, "Countertop", transform, new Vector3(cx, 0.93f, counterZ), new Vector3(usable + 0.16f, 0.07f, 1.2f), Quaternion.identity, top);

        DecoPart(PrimitiveType.Cube, "Sink", transform, new Vector3(left + 1.0f, 0.98f, counterZ), new Vector3(1.0f, 0.05f, 0.68f), Quaternion.identity, steel);
        DecoPart(PrimitiveType.Cylinder, "Faucet", transform, new Vector3(left + 1.0f, 1.14f, counterZ + 0.26f), new Vector3(0.03f, 0.3f, 0.03f), Quaternion.identity, steel);
        DecoPart(PrimitiveType.Cube, "Stove", transform, new Vector3(right - 1.1f, 0.98f, counterZ), new Vector3(1.1f, 0.05f, 0.68f), Quaternion.identity, MakeMaterial(new Color(0.12f, 0.13f, 0.15f), 0.2f, 0.5f));
        DecoPart(PrimitiveType.Cylinder, "Pot", transform, new Vector3(right - 1.1f, 1.06f, counterZ), new Vector3(0.15f, 0.12f, 0.15f), Quaternion.identity, steel);

        AddSolidBox("Fridge", new Vector3(right - 0.6f, 0.9f, cz - d * 0.5f + 0.75f), new Vector3(0.85f, 1.8f, 0.85f), new Color(0.78f, 0.8f, 0.82f));
        DecoPart(PrimitiveType.Cylinder, "Fridge Handle", transform, new Vector3(right - 0.6f, 1.4f, cz - d * 0.5f + 1.18f), new Vector3(0.02f, 0.3f, 0.02f), Quaternion.identity, steel);
    }

    private void BuildBathroom(float cx, float cz, float w, float d)
    {
        float left = cx - w * 0.5f, right = cx + w * 0.5f;
        float back = cz + d * 0.5f;
        BuildToilet(right - 0.55f, cz - 0.2f, 270f);        // 马桶靠东墙
        BuildWashbasin(left + 0.5f, cz - 0.2f, 90f);        // 洗手台靠西墙
        BuildBathtub(cx, back - 0.62f, 0f);                 // 浴缸沿北墙
    }

    private void BuildBedroom(float cx, float cz, float w, float d)
    {
        float left = cx - w * 0.5f, right = cx + w * 0.5f;
        float front = cz - d * 0.5f, back = cz + d * 0.5f;
        BuildBed(cx - 0.3f, back - 1.55f, 180f);            // 床头靠北墙
        BuildCabinet(Mathf.Min(cx + 1.45f, right - 0.55f), back - 0.7f, 180f);
        BuildWardrobe(left + 0.5f, cz - 0.4f, 90f);         // 衣柜靠西墙
        BuildPlant(right - 0.6f, front + 0.7f);
    }

    // ── 家具构件 ──────────────────────────────────────────
    private Transform CreateGroup(string name, Vector3 position, float yaw)
    {
        GameObject group = new GameObject(name);
        group.transform.SetParent(transform, false);
        group.transform.position = position;
        group.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        generatedObjects.Add(group);
        return group.transform;
    }

    private GameObject DecoPart(PrimitiveType type, string name, Transform parent, Vector3 localPosition, Vector3 localScale, Quaternion localRotation, Material material)
    {
        GameObject part = MakePrimitive(type, name, parent, localPosition, localScale, localRotation, material);
        Collider collider = part.GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = false;
        }
        return part;
    }

    private GameObject CreateDecoCylinder(string name, Vector3 position, float radius, float height, Quaternion rotation, Material material)
    {
        GameObject cylinder = CreateCylinder(name, position, radius, height, rotation, material);
        Collider collider = cylinder.GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = false;
        }
        return cylinder;
    }

    private GameObject CreateDecoSphere(string name, Vector3 position, float radius, Material material)
    {
        GameObject sphere = MakePrimitive(PrimitiveType.Sphere, name, transform, Vector3.zero, Vector3.one * radius * 2f, Quaternion.identity, material);
        sphere.transform.position = position;
        Collider collider = sphere.GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = false;
        }
        generatedObjects.Add(sphere);
        return sphere;
    }

    // 旋转家具的轴对齐包围盒，用于碰撞
    private void AddRotatedObstacle(string name, Vector3 center, Vector3 size, float yaw)
    {
        float rad = yaw * Mathf.Deg2Rad;
        float c = Mathf.Abs(Mathf.Cos(rad));
        float s = Mathf.Abs(Mathf.Sin(rad));
        float halfX = (c * size.x + s * size.z) * 0.5f;
        float halfZ = (s * size.x + c * size.z) * 0.5f;
        obstacles.Add(new Bounds(center, new Vector3(halfX * 2f, size.y, halfZ * 2f)));
    }

    private void BuildChair(float x, float z, float yaw)
    {
        Transform g = CreateGroup("Chair", new Vector3(x, 0f, z), yaw);
        Material wood = MakeMaterial(new Color(0.56f, 0.39f, 0.23f), 0.02f, 0.35f);
        Material cushion = MakeMaterial(new Color(0.76f, 0.38f, 0.32f), 0.02f, 0.4f);

        DecoPart(PrimitiveType.Cube, "Seat", g, new Vector3(0f, 0.45f, 0f), new Vector3(0.46f, 0.08f, 0.46f), Quaternion.identity, cushion);
        DecoPart(PrimitiveType.Cube, "Back", g, new Vector3(0f, 0.73f, -0.2f), new Vector3(0.46f, 0.5f, 0.07f), Quaternion.identity, wood);
        for (int i = 0; i < 4; i++)
        {
            float ox = (i % 2 == 0) ? -0.19f : 0.19f;
            float oz = (i < 2) ? -0.19f : 0.19f;
            DecoPart(PrimitiveType.Cube, "Leg", g, new Vector3(ox, 0.21f, oz), new Vector3(0.05f, 0.42f, 0.05f), Quaternion.identity, wood);
        }
        AddRotatedObstacle("Chair Body", new Vector3(x, 0.45f, z), new Vector3(0.5f, 0.9f, 0.5f), yaw);
    }

    private void BuildTable(float x, float z, float sizeX, float sizeZ, float yaw, float height)
    {
        Transform g = CreateGroup("Table", new Vector3(x, 0f, z), yaw);
        EnsureTextures();
        Material wood = TexturedMaterial(new Color(0.6f, 0.43f, 0.26f), woodTexture, 2f);

        DecoPart(PrimitiveType.Cube, "Top", g, new Vector3(0f, height, 0f), new Vector3(sizeX, 0.08f, sizeZ), Quaternion.identity, wood);
        float lx = sizeX * 0.5f - 0.12f;
        float lz = sizeZ * 0.5f - 0.12f;
        for (int i = 0; i < 4; i++)
        {
            float ox = (i % 2 == 0) ? -lx : lx;
            float oz = (i < 2) ? -lz : lz;
            DecoPart(PrimitiveType.Cube, "Leg", g, new Vector3(ox, height * 0.5f, oz), new Vector3(0.08f, height, 0.08f), Quaternion.identity, wood);
        }
        AddRotatedObstacle("Table Body", new Vector3(x, height * 0.5f, z), new Vector3(sizeX, height, sizeZ), yaw);
    }

    private void BuildBed(float x, float z, float yaw)
    {
        Transform g = CreateGroup("Bed", new Vector3(x, 0f, z), yaw);
        Material frame = MakeMaterial(new Color(0.45f, 0.3f, 0.18f), 0.02f, 0.4f);
        Material sheet = MakeMaterial(new Color(0.75f, 0.79f, 0.87f), 0.02f, 0.4f);
        Material pillow = MakeMaterial(new Color(0.96f, 0.96f, 0.93f), 0.02f, 0.4f);
        Material quilt = MakeMaterial(new Color(0.5f, 0.56f, 0.74f), 0.02f, 0.4f);

        DecoPart(PrimitiveType.Cube, "Frame", g, new Vector3(0f, 0.2f, 0f), new Vector3(1.6f, 0.4f, 2.05f), Quaternion.identity, frame);
        DecoPart(PrimitiveType.Cube, "Mattress", g, new Vector3(0f, 0.5f, 0f), new Vector3(1.5f, 0.22f, 1.95f), Quaternion.identity, sheet);
        DecoPart(PrimitiveType.Cube, "Headboard", g, new Vector3(0f, 0.78f, -1.05f), new Vector3(1.6f, 1.15f, 0.12f), Quaternion.identity, frame);
        DecoPart(PrimitiveType.Cube, "Pillow", g, new Vector3(0f, 0.65f, -0.72f), new Vector3(1.1f, 0.14f, 0.42f), Quaternion.identity, pillow);
        DecoPart(PrimitiveType.Cube, "Quilt", g, new Vector3(0f, 0.63f, 0.35f), new Vector3(1.52f, 0.09f, 1.15f), Quaternion.identity, quilt);
        AddRotatedObstacle("Bed Body", new Vector3(x, 0.3f, z), new Vector3(1.7f, 0.6f, 2.15f), yaw);
    }

    private void BuildSofa(float x, float z, float yaw)
    {
        Transform g = CreateGroup("Sofa", new Vector3(x, 0f, z), yaw);
        EnsureTextures();
        Material fabric = TexturedMaterial(new Color(0.4f, 0.47f, 0.53f), fabricTexture, 4f);
        Material cushion = TexturedMaterial(new Color(0.5f, 0.58f, 0.64f), fabricTexture, 4f);

        DecoPart(PrimitiveType.Cube, "Base", g, new Vector3(0f, 0.22f, 0f), new Vector3(2f, 0.44f, 0.85f), Quaternion.identity, fabric);
        DecoPart(PrimitiveType.Cube, "Back", g, new Vector3(0f, 0.62f, -0.36f), new Vector3(2f, 0.55f, 0.14f), Quaternion.identity, fabric);
        DecoPart(PrimitiveType.Cube, "Arm L", g, new Vector3(-0.93f, 0.55f, 0f), new Vector3(0.14f, 0.35f, 0.85f), Quaternion.identity, fabric);
        DecoPart(PrimitiveType.Cube, "Arm R", g, new Vector3(0.93f, 0.55f, 0f), new Vector3(0.14f, 0.35f, 0.85f), Quaternion.identity, fabric);
        DecoPart(PrimitiveType.Cube, "Cushion L", g, new Vector3(-0.47f, 0.48f, 0.06f), new Vector3(0.82f, 0.13f, 0.66f), Quaternion.identity, cushion);
        DecoPart(PrimitiveType.Cube, "Cushion R", g, new Vector3(0.47f, 0.48f, 0.06f), new Vector3(0.82f, 0.13f, 0.66f), Quaternion.identity, cushion);
        AddRotatedObstacle("Sofa Body", new Vector3(x, 0.4f, z), new Vector3(2.1f, 0.8f, 0.95f), yaw);
    }

    private void BuildWardrobe(float x, float z, float yaw)
    {
        Transform g = CreateGroup("Wardrobe", new Vector3(x, 0f, z), yaw);
        EnsureTextures();
        Material body = TexturedMaterial(new Color(0.5f, 0.35f, 0.2f), woodTexture, 2f);
        Material door = TexturedMaterial(new Color(0.62f, 0.46f, 0.28f), woodTexture, 2f);
        Material handle = MakeMaterial(new Color(0.82f, 0.78f, 0.45f), 0.7f, 0.6f);

        DecoPart(PrimitiveType.Cube, "Body", g, new Vector3(0f, 1.1f, 0f), new Vector3(1.7f, 2.2f, 0.6f), Quaternion.identity, body);
        DecoPart(PrimitiveType.Cube, "Door L", g, new Vector3(-0.42f, 1.1f, -0.32f), new Vector3(0.8f, 2.05f, 0.05f), Quaternion.identity, door);
        DecoPart(PrimitiveType.Cube, "Door R", g, new Vector3(0.42f, 1.1f, -0.32f), new Vector3(0.8f, 2.05f, 0.05f), Quaternion.identity, door);
        DecoPart(PrimitiveType.Cylinder, "Handle L", g, new Vector3(-0.06f, 1.1f, -0.36f), new Vector3(0.025f, 0.16f, 0.025f), Quaternion.identity, handle);
        DecoPart(PrimitiveType.Cylinder, "Handle R", g, new Vector3(0.06f, 1.1f, -0.36f), new Vector3(0.025f, 0.16f, 0.025f), Quaternion.identity, handle);
        AddRotatedObstacle("Wardrobe Body", new Vector3(x, 1.1f, z), new Vector3(1.8f, 2.2f, 0.7f), yaw);
    }

    private void BuildCabinet(float x, float z, float yaw)
    {
        Transform g = CreateGroup("Cabinet", new Vector3(x, 0f, z), yaw);
        Material body = MakeMaterial(new Color(0.58f, 0.6f, 0.62f), 0.35f, 0.5f);
        Material drawer = MakeMaterial(new Color(0.7f, 0.72f, 0.74f), 0.35f, 0.5f);

        DecoPart(PrimitiveType.Cube, "Body", g, new Vector3(0f, 0.6f, 0f), new Vector3(0.9f, 1.2f, 0.5f), Quaternion.identity, body);
        for (int i = 0; i < 3; i++)
        {
            DecoPart(PrimitiveType.Cube, "Drawer", g, new Vector3(0f, 0.3f + i * 0.37f, -0.26f), new Vector3(0.78f, 0.3f, 0.05f), Quaternion.identity, drawer);
        }
        AddRotatedObstacle("Cabinet Body", new Vector3(x, 0.6f, z), new Vector3(1f, 1.2f, 0.6f), yaw);
    }

    private void BuildToilet(float x, float z, float yaw)
    {
        Transform g = CreateGroup("Toilet", new Vector3(x, 0f, z), yaw);
        Material white = MakeMaterial(new Color(0.95f, 0.96f, 0.96f), 0.05f, 0.65f);

        DecoPart(PrimitiveType.Cube, "Tank", g, new Vector3(0f, 0.74f, -0.28f), new Vector3(0.48f, 0.58f, 0.22f), Quaternion.identity, white);
        DecoPart(PrimitiveType.Cylinder, "Pedestal", g, new Vector3(0f, 0.2f, 0.08f), new Vector3(0.16f, 0.2f, 0.2f), Quaternion.identity, white);
        DecoPart(PrimitiveType.Cylinder, "Bowl", g, new Vector3(0f, 0.36f, 0.05f), new Vector3(0.26f, 0.1f, 0.32f), Quaternion.identity, white);
        DecoPart(PrimitiveType.Cube, "Seat", g, new Vector3(0f, 0.46f, 0.03f), new Vector3(0.48f, 0.06f, 0.6f), Quaternion.identity, white);
        AddRotatedObstacle("Toilet Body", new Vector3(x, 0.4f, z), new Vector3(0.55f, 0.8f, 0.75f), yaw);
    }

    private void BuildBathtub(float x, float z, float yaw)
    {
        Transform g = CreateGroup("Bathtub", new Vector3(x, 0f, z), yaw);
        Material shell = MakeMaterial(new Color(0.92f, 0.94f, 0.95f), 0.05f, 0.65f);
        Material water = MakeMaterial(new Color(0.6f, 0.78f, 0.85f), 0.1f, 0.8f);

        DecoPart(PrimitiveType.Cube, "Shell", g, new Vector3(0f, 0.3f, 0f), new Vector3(1.7f, 0.6f, 0.8f), Quaternion.identity, shell);
        DecoPart(PrimitiveType.Cube, "Water", g, new Vector3(0f, 0.48f, 0f), new Vector3(1.5f, 0.1f, 0.62f), Quaternion.identity, water);
        DecoPart(PrimitiveType.Cylinder, "Tap", g, new Vector3(0f, 0.68f, -0.34f), new Vector3(0.03f, 0.16f, 0.03f), Quaternion.identity, shell);
        AddRotatedObstacle("Bathtub Body", new Vector3(x, 0.3f, z), new Vector3(1.8f, 0.6f, 0.9f), yaw);
    }

    private void BuildWashbasin(float x, float z, float yaw)
    {
        Transform g = CreateGroup("Washbasin", new Vector3(x, 0f, z), yaw);
        Material cab = MakeMaterial(new Color(0.56f, 0.43f, 0.31f), 0.02f, 0.4f);
        Material basin = MakeMaterial(new Color(0.95f, 0.96f, 0.96f), 0.05f, 0.65f);
        Material mirror = MakeMaterial(new Color(0.8f, 0.89f, 0.92f), 0.7f, 0.9f);

        DecoPart(PrimitiveType.Cube, "Cabinet", g, new Vector3(0f, 0.4f, 0f), new Vector3(0.9f, 0.8f, 0.5f), Quaternion.identity, cab);
        DecoPart(PrimitiveType.Cube, "Basin", g, new Vector3(0f, 0.84f, 0f), new Vector3(1f, 0.12f, 0.56f), Quaternion.identity, basin);
        DecoPart(PrimitiveType.Cylinder, "Faucet", g, new Vector3(0f, 0.98f, -0.2f), new Vector3(0.03f, 0.14f, 0.03f), Quaternion.identity, basin);
        DecoPart(PrimitiveType.Cube, "Mirror", g, new Vector3(0f, 1.6f, -0.24f), new Vector3(0.7f, 0.9f, 0.05f), Quaternion.identity, mirror);
        AddRotatedObstacle("Washbasin Body", new Vector3(x, 0.45f, z), new Vector3(1f, 0.9f, 0.6f), yaw);
    }

    private void BuildTvUnit(float x, float z, float yaw)
    {
        Transform g = CreateGroup("TV Unit", new Vector3(x, 0f, z), yaw);
        Material cab = MakeMaterial(new Color(0.6f, 0.44f, 0.27f), 0.02f, 0.4f);
        Material screen = MakeMaterial(new Color(0.07f, 0.08f, 0.1f), 0.35f, 0.8f);

        DecoPart(PrimitiveType.Cube, "Stand", g, new Vector3(0f, 0.24f, 0f), new Vector3(1.9f, 0.48f, 0.45f), Quaternion.identity, cab);
        DecoPart(PrimitiveType.Cube, "TV Base", g, new Vector3(0f, 0.52f, 0f), new Vector3(0.4f, 0.06f, 0.24f), Quaternion.identity, screen);
        DecoPart(PrimitiveType.Cube, "TV", g, new Vector3(0f, 0.95f, 0.02f), new Vector3(1.45f, 0.82f, 0.06f), Quaternion.identity, screen);
        AddRotatedObstacle("TV Unit Body", new Vector3(x, 0.3f, z), new Vector3(2f, 0.6f, 0.55f), yaw);
    }

    private void BuildPlant(float x, float z)
    {
        Transform g = CreateGroup("Plant", new Vector3(x, 0f, z), 0f);
        Material pot = MakeMaterial(new Color(0.64f, 0.42f, 0.3f), 0.02f, 0.4f);
        Material leaf = MakeMaterial(new Color(0.25f, 0.53f, 0.27f), 0.02f, 0.4f);

        DecoPart(PrimitiveType.Cylinder, "Pot", g, new Vector3(0f, 0.22f, 0f), new Vector3(0.24f, 0.22f, 0.24f), Quaternion.identity, pot);
        DecoPart(PrimitiveType.Sphere, "Leaf 1", g, new Vector3(0f, 0.66f, 0f), Vector3.one * 0.52f, Quaternion.identity, leaf);
        DecoPart(PrimitiveType.Sphere, "Leaf 2", g, new Vector3(0.18f, 0.88f, 0.1f), Vector3.one * 0.36f, Quaternion.identity, leaf);
        DecoPart(PrimitiveType.Sphere, "Leaf 3", g, new Vector3(-0.16f, 0.84f, -0.12f), Vector3.one * 0.32f, Quaternion.identity, leaf);
        AddRotatedObstacle("Plant Body", new Vector3(x, 0.25f, z), new Vector3(0.5f, 0.5f, 0.5f), 0f);
    }

    // ── 几何/碰撞辅助 ─────────────────────────────────────
    private void BuildRoomFloor(float xMin, float xMax, float zMin, float zMax, Color color, bool wood)
    {
        EnsureTextures();
        float cx = (xMin + xMax) * 0.5f;
        float cz = (zMin + zMax) * 0.5f;
        Material floorSurface = wood
            ? TexturedMaterial(color, woodTexture, 5f)
            : TexturedMaterial(color, tileTexture, 5f);
        CreateDecoCube("Room Floor", new Vector3(cx, 0.005f, cz), new Vector3(xMax - xMin, 0.01f, zMax - zMin), floorSurface);
        // 踢脚线
        Color trim = new Color(0.45f, 0.36f, 0.27f);
        CreateDecoCube("Skirting", new Vector3(cx, 0.07f, zMax - 0.12f), new Vector3(xMax - xMin, 0.14f, 0.05f), trim);
        CreateDecoCube("Skirting", new Vector3(cx, 0.07f, zMin + 0.12f), new Vector3(xMax - xMin, 0.14f, 0.05f), trim);
    }

    private void BuildCheckeredFloor(float xMin, float xMax, float zMin, float zMax)
    {
        int tilesX = Mathf.CeilToInt(xMax - xMin);
        int tilesZ = Mathf.CeilToInt(zMax - zMin);
        for (int ix = 0; ix < tilesX; ix++)
        {
            for (int iz = 0; iz < tilesZ; iz++)
            {
                float x = xMin + ix + 0.5f;
                float z = zMin + iz + 0.5f;
                bool light = ((ix + iz) & 1) == 0;
                CreateDecoCube("Tile", new Vector3(x, 0.005f, z), new Vector3(0.97f, 0.01f, 0.97f), light ? floorA : floorB);
            }
        }
    }

    private void AddSolidBox(string name, Vector3 center, Vector3 size, Color color)
    {
        CreateCube(name, center, size, color);
        obstacles.Add(new Bounds(center, size));
    }

    private void AddWallWithDoorX(float x, float zMin, float zMax, float height, float thickness, float doorMin, float doorMax, Color color)
    {
        // 垂直墙（沿 Z 方向），X 固定，带门洞 [doorMin, doorMax]
        if (doorMin > zMin)
        {
            float zc = (zMin + doorMin) * 0.5f;
            AddSolidBox("Wall", new Vector3(x, height * 0.5f, zc), new Vector3(thickness, height, doorMin - zMin), color);
        }
        if (doorMax < zMax)
        {
            float zc = (doorMax + zMax) * 0.5f;
            AddSolidBox("Wall", new Vector3(x, height * 0.5f, zc), new Vector3(thickness, height, zMax - doorMax), color);
        }
        // 门框上方的过梁
        float midZ = (doorMin + doorMax) * 0.5f;
        float midH = doorMax - doorMin;
        if (midH > 0f && height > DoorHeight)
        {
            CreateDecoCube("Lintel", new Vector3(x, (DoorHeight + height) * 0.5f, midZ), new Vector3(thickness, height - DoorHeight, midH), color);
        }
    }

    private void AddWallWithDoorZ(float z, float xMin, float xMax, float height, float thickness, float doorMin, float doorMax, Color color)
    {
        // 水平墙（沿 X 方向），Z 固定，带门洞 [doorMin, doorMax]
        if (doorMin > xMin)
        {
            float xc = (xMin + doorMin) * 0.5f;
            AddSolidBox("Wall", new Vector3(xc, height * 0.5f, z), new Vector3(doorMin - xMin, height, thickness), color);
        }
        if (doorMax < xMax)
        {
            float xc = (doorMax + xMax) * 0.5f;
            AddSolidBox("Wall", new Vector3(xc, height * 0.5f, z), new Vector3(xMax - doorMax, height, thickness), color);
        }
        float midX = (doorMin + doorMax) * 0.5f;
        float midW = doorMax - doorMin;
        if (midW > 0f && height > DoorHeight)
        {
            CreateDecoCube("Lintel", new Vector3(midX, (DoorHeight + height) * 0.5f, z), new Vector3(midW, height - DoorHeight, thickness), color);
        }
    }

    private void AddHorizontalWallWithDoors(float z, float xMin, float xMax, float height, float thickness, Color color, params float[] doors)
    {
        // 水平墙（沿 X 方向），doors 是成对的门洞 [start, end]
        float current = xMin;
        for (int i = 0; i < doors.Length; i += 2)
        {
            float doorStart = doors[i];
            float doorEnd = doors[i + 1];
            if (doorStart > current)
            {
                AddSolidBox("Wall", new Vector3((current + doorStart) * 0.5f, height * 0.5f, z), new Vector3(doorStart - current, height, thickness), color);
            }
            CreateDecoCube("Lintel", new Vector3((doorStart + doorEnd) * 0.5f, (DoorHeight + height) * 0.5f, z), new Vector3(doorEnd - doorStart, height - DoorHeight, thickness), color);
            current = doorEnd;
        }
        if (current < xMax)
        {
            AddSolidBox("Wall", new Vector3((current + xMax) * 0.5f, height * 0.5f, z), new Vector3(xMax - current, height, thickness), color);
        }
    }

    // ── 相机 ──────────────────────────────────────────────
    private void BuildCamera()
    {
        GameObject cameraObject = new GameObject("First Person Camera");
        viewCamera = cameraObject.AddComponent<Camera>();
        cameraObject.AddComponent<AudioListener>();
        cameraObject.tag = "MainCamera";
        viewCamera.fieldOfView = 68f;
        viewCamera.nearClipPlane = 0.06f;
        viewCamera.farClipPlane = 400f;
        viewCamera.clearFlags = CameraClearFlags.SolidColor;
        viewCamera.backgroundColor = new Color(0.53f, 0.76f, 0.94f); // 蓝天
        viewCamera.transform.position = spawnPosition + Vector3.up * EyeHeight;

        // 全局色调：饱和度/曝光/对比/色阶
        Shader gradeShader = Shader.Find("Custom/ColorGrade");
        if (gradeShader != null)
        {
            ColorGradeEffect grade = cameraObject.AddComponent<ColorGradeEffect>();
            grade.gradeMaterial = new Material(gradeShader);
        }
    }

    // ── 第一人称视角 ──────────────────────────────────────
    private void HandleLook()
    {
        if (viewCamera == null || player == null)
        {
            return;
        }

        // 工具切换：Q 键 / 鼠标滚轮；B 键开关背包
        if (Input.GetKeyDown(KeyCode.Q))
        {
            NextTool(1);
        }
        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Abs(scroll) > 0.01f)
        {
            NextTool(scroll > 0f ? 1 : -1);
        }
        if (Input.GetKeyDown(KeyCode.B))
        {
            bagOpen = !bagOpen;
            if (bagOpen)
            {
                shopOpen = false;
            }
        }
        if (Input.GetKeyDown(KeyCode.M))
        {
            minimapLarge = !minimapLarge;
        }
        if (Input.GetKeyDown(KeyCode.G))
        {
            shopOpen = !shopOpen;
            if (shopOpen)
            {
                bagOpen = false;   // 两个面板同位置，互斥
            }
        }
        if (Input.GetKeyDown(KeyCode.N))
        {
            almanacOpen = !almanacOpen;
        }
        if (Input.GetKeyDown(KeyCode.T))
        {
            twinPanelOpen = !twinPanelOpen;
            if (twinPanelOpen)
            {
                SetCursorLock(false);   // 俯视图要点楼栋，打开面板时召唤鼠标
            }
        }
        if (Input.GetKeyDown(KeyCode.P))
        {
            ToggleMute();
        }
        if (Input.GetKeyDown(KeyCode.V))
        {
            thirdPerson = !thirdPerson;
            SetHeadVisible(thirdPerson);
            ShowToast(thirdPerson ? "第三人称视角（V 切回第一人称）" : "第一人称视角", 2.5f);
        }
        if (Input.GetKeyDown(KeyCode.R) && dialogueIndex < 0)
        {
            RestUntilMorning();
        }

        // 鼠标模式：按 Tab 切换（显示/锁定），Esc 释放；界面开着时不自动锁回去
        bool mouseNeeded = lobbyOpen || shopOpen || bagOpen || almanacOpen || twinPanelOpen || faultManualOpen;
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            SetCursorLock(!cursorLocked);
        }
        else if (Input.GetKeyDown(KeyCode.Escape))
        {
            SetCursorLock(false);
        }
        else if (loggedIn && !cursorLocked && !mouseNeeded
            && Input.GetMouseButtonDown(0) && !IsPointerOverGui(Input.mousePosition))
        {
            // 没有界面打开时，点非 UI 区域才重新锁定鼠标
            SetCursorLock(true);
        }

        if (cursorLocked)
        {
            lookYaw += Input.GetAxis("Mouse X") * MouseSensitivity;
            lookPitch = Mathf.Clamp(lookPitch - Input.GetAxis("Mouse Y") * MouseSensitivity, -75f, 75f);
        }

        player.transform.rotation = Quaternion.Euler(0f, lookYaw, 0f);
        viewCamera.transform.rotation = Quaternion.Euler(lookPitch, lookYaw, 0f);

        if (thirdPerson)
        {
            // 第三人称：相机在角色身后，遇到墙自动拉近
            Vector3 dir = Quaternion.Euler(lookPitch, lookYaw, 0f) * Vector3.forward;
            Vector3 pivot = playerPosition + Vector3.up * 1.45f;
            float distance = ThirdPersonDistance;
            for (float d = 0.6f; d <= ThirdPersonDistance; d += 0.3f)
            {
                if (Collides(pivot - dir * d, true))
                {
                    distance = Mathf.Max(0.8f, d - 0.4f);
                    break;
                }
            }
            viewCamera.transform.position = pivot - dir * distance;
        }
        else
        {
            viewCamera.transform.position = playerPosition + Vector3.up * EyeHeight;
        }
    }

    private void SetHeadVisible(bool visible)
    {
        if (headVisible == visible || playerHeadRenderers == null)
        {
            return;
        }
        headVisible = visible;
        for (int i = 0; i < playerHeadRenderers.Length - 1; i++)
        {
            if (playerHeadRenderers[i] != null)
            {
                playerHeadRenderers[i].enabled = visible;   // 头部/安全帽/反光背心
            }
        }
    }

    // 某个位置附近是否有 NPC（同事 / 户主 / 工头）
    private bool AnyoneNear(Vector3 point, float radius)
    {
        for (int i = 0; i < colleagues.Count; i++)
        {
            if (colleagues[i].rig != null && colleagues[i].rig.root != null
                && colleagues[i].rig.root.gameObject.activeSelf
                && Distance2D(colleagues[i].rig.root.position, point) < radius)
            {
                return true;
            }
        }
        for (int i = 0; i < homeowners.Count; i++)
        {
            if (homeowners[i].rig != null && homeowners[i].rig.root != null
                && Distance2D(homeowners[i].rig.root.position, point) < radius)
            {
                return true;
            }
        }
        return false;
    }

    private void SetCursorLock(bool locked)
    {
        cursorLocked = locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    // ── 登录界面 ──────────────────────────────────────────
    private void ApplyAppearance(int coat, int trouser)
    {
        custCoat = Mathf.Clamp(coat, 0, CoatPalette.Length - 1);
        custTrouser = Mathf.Clamp(trouser, 0, TrouserPalette.Length - 1);
        if (playerClothMaterial != null)
        {
            playerClothMaterial.color = CoatPalette[custCoat];
        }
        if (playerTrouserMaterial != null)
        {
            playerTrouserMaterial.color = TrouserPalette[custTrouser];
        }
        // 同步到服装列表的第一件（标准工装），让换装界面显示一致
        if (outfits.Count > 0)
        {
            outfits[0].coat = CoatPalette[custCoat];
            outfits[0].trouser = TrouserPalette[custTrouser];
        }
    }

    // ── Supabase 云端接口 ────────────────────────────────
    [System.Serializable]
    private class AccountRow
    {
        public string username;
        public string password_hash;
        public string display_name;
        public bool intro_seen;
        public int coat;
        public int trouser;
    }

    [System.Serializable]
    private class AccountRows { public AccountRow[] items; }

    [System.Serializable]
    private class SaveRow { public SaveData data; }

    [System.Serializable]
    private class SaveRows { public SaveRow[] items; }

    private System.Collections.IEnumerator SupabaseRequest(string method, string path, string bodyJson, System.Action<string> onResult, string prefer = null)
    {
        UnityWebRequest req = method == "GET"
            ? UnityWebRequest.Get(SupabaseUrl + path)
            : new UnityWebRequest(SupabaseUrl + path, method);
        req.SetRequestHeader("apikey", SupabaseKey);
        req.SetRequestHeader("Content-Type", "application/json");
        if (!string.IsNullOrEmpty(prefer))
        {
            req.SetRequestHeader("Prefer", prefer);
        }
        if (!string.IsNullOrEmpty(bodyJson))
        {
            req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(bodyJson));
        }
        req.downloadHandler = new DownloadHandlerBuffer();
        yield return req.SendWebRequest();
        string text = req.downloadHandler != null ? req.downloadHandler.text : "";
        if (onResult != null)
        {
            onResult(req.result == UnityWebRequest.Result.Success && !string.IsNullOrEmpty(text) ? text : null);
        }
        req.Dispose();
    }

    private System.Collections.IEnumerator LoginRoutine(string user, string pass)
    {
        string path = "/rest/v1/accounts?username=eq." + UnityWebRequest.EscapeURL(user) + "&select=*";
        string result = null;
        yield return SupabaseRequest("GET", path, null, (r) => result = r);
        if (result == null)
        {
            // 云端不可用：回退本地登录，保证游戏不被卡死
            string name;
            int coat, trouser;
            if (AccountStore.TryLoad(user, pass, out name, out coat, out trouser))
            {
                ApplyAppearance(coat, trouser);
                displayName = name;
                EnterGame(user);
                loginMessage = "云端不可用，已用本地存档登录";
            }
            else
            {
                loginMessage = "无法连接服务器，请检查网络或稍后再试";
            }
            authBusy = false;
            yield break;
        }
        AccountRows rows = JsonUtility.FromJson<AccountRows>("{\"items\":" + result + "}");
        if (rows == null || rows.items == null || rows.items.Length == 0)
        {
            loginMessage = "该用户名不存在，请先注册";
            authBusy = false;
            yield break;
        }
        AccountRow row = rows.items[0];
        if (row.password_hash != AccountStore.Hash(pass))
        {
            loginMessage = "密码不正确";
            authBusy = false;
            yield break;
        }
        ApplyAppearance(row.coat, row.trouser);
        displayName = row.display_name;
        introSeenCloud = row.intro_seen;
        EnterGame(user);
        authBusy = false;
    }

    private System.Collections.IEnumerator RegisterRoutine(string user, string pass)
    {
        string path = "/rest/v1/accounts?username=eq." + UnityWebRequest.EscapeURL(user) + "&select=username";
        string result = null;
        yield return SupabaseRequest("GET", path, null, (r) => result = r);
        if (result != null && result != "[]")
        {
            loginMessage = "该用户名已被注册";
            authBusy = false;
            yield break;
        }
        string body = "{\"username\":\"" + user + "\",\"password_hash\":\"" + AccountStore.Hash(pass)
            + "\",\"display_name\":\"" + loginName + "\",\"coat\":" + custCoat + ",\"trouser\":" + custTrouser + "}";
        string result2 = null;
        yield return SupabaseRequest("POST", "/rest/v1/accounts", body, (r) => result2 = r);
        if (result2 == null)
        {
            // 云端不可用：回退本地注册，保证游戏不被卡死
            if (AccountStore.Exists(user))
            {
                loginMessage = "该用户名已被注册";
                authBusy = false;
                yield break;
            }
            AccountStore.Save(user, pass, loginName, custCoat, custTrouser);
            displayName = loginName;
            loginMessage = "云端不可用，已本地注册并登录";
            EnterGame(user);
            authBusy = false;
            yield break;
        }
        displayName = loginName;
        introSeenCloud = false;
        loginMessage = "注册成功，已自动登录";
        EnterGame(user);
        authBusy = false;
    }

    private System.Collections.IEnumerator SaveToCloud(string username, string saveJson)
    {
        string body = "{\"username\":\"" + username + "\",\"data\":" + saveJson + "}";
        string result = null;
        yield return SupabaseRequest("POST", "/rest/v1/saves", body, (r) => result = r, "resolution=merge-duplicates");
        // 自动存档失败静默，避免打断游戏
    }

    private System.Collections.IEnumerator LoadFromCloud(string username)
    {
        string path = "/rest/v1/saves?username=eq." + UnityWebRequest.EscapeURL(username) + "&select=data";
        string result = null;
        yield return SupabaseRequest("GET", path, null, (r) => result = r);
        if (result == null || result == "[]")
        {
            LoadGame();   // 云端无数据，回退本地缓存
            yield break;
        }
        SaveRows rows = JsonUtility.FromJson<SaveRows>("{\"items\":" + result + "}");
        if (rows == null || rows.items == null || rows.items.Length == 0 || rows.items[0].data == null)
        {
            LoadGame();
            yield break;
        }
        ApplySaveData(rows.items[0].data);
        ShowToast("已从云端载入存档：累计收入 ¥" + income.ToString("N0") + "，成本 ¥" + expenses.ToString("N0")
            + "，工单 " + orders.Count + " 单", 5f);
    }

    // ── 联机房间 / 聊天 DTO ──────────────────────────────
    [System.Serializable] private class RoomRow { public string id; public string code; }
    [System.Serializable] private class RoomRows { public RoomRow[] items; }
    [System.Serializable] private class RoomPlayerRow { public string username; public string display_name; public int coat; public int trouser; public double pos_x; public double pos_y; public double pos_z; public double rot_y; }
    [System.Serializable] private class RoomPlayerRows { public RoomPlayerRow[] items; }
    [System.Serializable] private class MessageRow { public long id; public string username; public string display_name; public string text; }
    [System.Serializable] private class MessageRows { public MessageRow[] items; }

    // JSON 字符串转义（中文保持原样，只转义引号/反斜杠/控制符，避免破坏 JSON 结构）
    private static string JsonEscape(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "";
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private string GenerateRoomCode()
    {
        // 纯 6 位数字房间号，避免字母/数字看混（5/S、2/Z、0/O 等）
        return UnityEngine.Random.Range(100000, 1000000).ToString();
    }

    private string BuildPlayerBody()
    {
        return "{\"room_id\":\"" + roomId + "\",\"username\":\"" + currentAccount
            + "\",\"display_name\":\"" + (string.IsNullOrEmpty(displayName) ? currentAccount : displayName)
            + "\",\"coat\":" + custCoat + ",\"trouser\":" + custTrouser
            + ",\"pos_x\":" + playerPosition.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            + ",\"pos_y\":" + playerPosition.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            + ",\"pos_z\":" + playerPosition.z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            + ",\"rot_y\":" + lookYaw.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "}";
    }

    private void CreateRoom()
    {
        PlayUiClick();
        roomCode = GenerateRoomCode();
        StartCoroutine(CreateRoomRoutine(roomCode));
    }

    private System.Collections.IEnumerator CreateRoomRoutine(string code)
    {
        string body = "{\"code\":\"" + code + "\"}";
        string result = null;
        yield return SupabaseRequest("POST", "/rest/v1/rooms?select=id,code", body, (r) => result = r, "return=representation");
        if (result == null)
        {
            roomMessage = "创建房间失败";
            yield break;
        }
        RoomRows rows = JsonUtility.FromJson<RoomRows>("{\"items\":" + result + "}");
        if (rows == null || rows.items == null || rows.items.Length == 0)
        {
            roomMessage = "创建房间失败";
            yield break;
        }
        roomId = rows.items[0].id;
        roomCode = code;
        isHost = true;
        lastUploadedSignature = int.MinValue;
        inRoom = true;
        roomMessage = "房间已创建，房间号 " + code + "（按 L 关闭面板）";
        yield return SupabaseRequest("POST", "/rest/v1/room_players", BuildPlayerBody(), null, "resolution=merge-duplicates");
    }

    private void JoinRoom()
    {
        PlayUiClick();
        if (string.IsNullOrEmpty(joinCode))
        {
            roomMessage = "请输入房间号";
            return;
        }
        StartCoroutine(JoinRoomRoutine(joinCode.Trim().ToUpperInvariant()));
    }

    private System.Collections.IEnumerator JoinRoomRoutine(string code)
    {
        string path = "/rest/v1/rooms?code=eq." + UnityWebRequest.EscapeURL(code) + "&select=id,code";
        string result = null;
        yield return SupabaseRequest("GET", path, null, (r) => result = r);
        if (result == null || result == "[]")
        {
            roomMessage = "房间不存在";
            yield break;
        }
        RoomRows rows = JsonUtility.FromJson<RoomRows>("{\"items\":" + result + "}");
        if (rows == null || rows.items == null || rows.items.Length == 0)
        {
            roomMessage = "房间不存在";
            yield break;
        }
        roomId = rows.items[0].id;
        roomCode = code;

        // 检查人数上限（最多 4 人）
        string countPath = "/rest/v1/room_players?room_id=eq." + roomId + "&select=username";
        string countResult = null;
        yield return SupabaseRequest("GET", countPath, null, (r) => countResult = r);
        if (countResult != null)
        {
            RoomPlayerRows pr = JsonUtility.FromJson<RoomPlayerRows>("{\"items\":" + countResult + "}");
            int existing = pr != null && pr.items != null ? pr.items.Length : 0;
            bool selfIn = pr != null && pr.items != null && System.Array.Exists(pr.items, x => x.username == currentAccount);
            if (!selfIn && existing >= 4)
            {
                roomMessage = "房间已满（最多 4 人）";
                yield break;
            }
        }

        isHost = false;
        lastUploadedSignature = int.MinValue;
        inRoom = true;
        roomMessage = "已加入房间 " + code;
        yield return SupabaseRequest("POST", "/rest/v1/room_players", BuildPlayerBody(), null, "resolution=merge-duplicates");
    }

    private void LeaveRoom()
    {
        inRoom = false;
        roomId = "";
        roomCode = "";
        foreach (var kv in remotePlayers)
        {
            if (kv.Value.root != null)
            {
                Destroy(kv.Value.root);
            }
        }
        remotePlayers.Clear();
        roomMessage = "已离开房间";
    }

    private void UpdatePresence()
    {
        if (!inRoom || string.IsNullOrEmpty(currentAccount) || currentAccount == "游客")
        {
            return;
        }
        StartCoroutine(SupabaseRequest("POST", "/rest/v1/room_players", BuildPlayerBody(), null, "resolution=merge-duplicates"));
    }

    private void PollPresence()
    {
        if (!inRoom)
        {
            return;
        }
        StartCoroutine(PollPresenceRoutine());
    }

    private System.Collections.IEnumerator PollPresenceRoutine()
    {
        string path = "/rest/v1/room_players?room_id=eq." + roomId + "&select=*";
        string result = null;
        yield return SupabaseRequest("GET", path, null, (r) => result = r);
        if (result == null)
        {
            yield break;
        }
        RoomPlayerRows rows = JsonUtility.FromJson<RoomPlayerRows>("{\"items\":" + result + "}");
        if (rows == null || rows.items == null)
        {
            yield break;
        }
        HashSet<string> alive = new HashSet<string>();
        for (int i = 0; i < rows.items.Length; i++)
        {
            RoomPlayerRow r = rows.items[i];
            if (r.username == currentAccount)
            {
                continue;   // 跳过自己
            }
            alive.Add(r.username);
            Vector3 target = new Vector3((float)r.pos_x, (float)r.pos_y, (float)r.pos_z);
            RemoteAvatar rp;
            if (!remotePlayers.TryGetValue(r.username, out rp))
            {
                rp = CreateRemoteAvatar(r.display_name, r.coat, r.trouser, target);
                remotePlayers[r.username] = rp;
            }
            rp.target = target;
            rp.targetYaw = (float)r.rot_y;
            if (rp.nameLabel != null && rp.nameLabel.text != r.display_name)
            {
                rp.nameLabel.text = r.display_name;
            }
            // 换装同步：颜色变了就刷新
            if (rp.coat != r.coat || rp.trouser != r.trouser)
            {
                ApplyAvatarOutfit(rp, r.coat, r.trouser);
            }
        }
        // 清理离线玩家
        var toRemove = new List<string>();
        foreach (var kv in remotePlayers)
        {
            if (!alive.Contains(kv.Key))
            {
                if (kv.Value.root != null)
                {
                    Destroy(kv.Value.root);
                }
                toRemove.Add(kv.Key);
            }
        }
        for (int i = 0; i < toRemove.Count; i++)
        {
            remotePlayers.Remove(toRemove[i]);
        }
    }

    // 远程玩家外观部件（便于换装时刷新颜色）
    private class RemoteAvatar
    {
        public GameObject root;
        public TextMesh nameLabel;
        public Vector3 target;
        public float targetYaw;
        public int coat;
        public int trouser;
        public readonly List<Renderer> coatRenderers = new List<Renderer>();
        public readonly List<Renderer> trouserRenderers = new List<Renderer>();
        public float animTime;
        public Vector3 lastPos;
    }

    private void AddAvatarPart(RemoteAvatar av, PrimitiveType type, string partName, Vector3 localPos, Vector3 localScale, bool isCoat, bool isTrouser)
    {
        GameObject part = GameObject.CreatePrimitive(type);
        part.name = partName;
        part.transform.SetParent(av.root.transform, false);
        part.transform.localPosition = localPos;
        part.transform.localScale = localScale;
        Collider col = part.GetComponent<Collider>();
        if (col != null)
        {
            Destroy(col);
        }
        Renderer r = part.GetComponent<Renderer>();
        if (isCoat)
        {
            av.coatRenderers.Add(r);
        }
        else if (isTrouser)
        {
            av.trouserRenderers.Add(r);
        }
        else
        {
            r.sharedMaterial = SimpleMaterial(new Color(0.84f, 0.66f, 0.5f));   // 皮肤
        }
    }

    private RemoteAvatar CreateRemoteAvatar(string name, int coat, int trouser, Vector3 pos)
    {
        GameObject root = new GameObject("Remote_" + name);
        root.transform.SetParent(transform, false);
        root.transform.position = pos;

        RemoteAvatar av = new RemoteAvatar { root = root, target = pos, coat = coat, trouser = trouser };

        // 方块人：躯干 + 四肢 + 头 + 安全帽（和玩家同款造型）
        AddAvatarPart(av, PrimitiveType.Cube, "Torso", new Vector3(0f, 0.85f, 0f), new Vector3(0.5f, 0.7f, 0.3f), true, false);
        AddAvatarPart(av, PrimitiveType.Cube, "Waist", new Vector3(0f, 0.68f, 0f), new Vector3(0.4f, 0.3f, 0.26f), true, false);
        AddAvatarPart(av, PrimitiveType.Cube, "ShoulderL", new Vector3(-0.29f, 1.16f, 0f), new Vector3(0.16f, 0.14f, 0.2f), true, false);
        AddAvatarPart(av, PrimitiveType.Cube, "ShoulderR", new Vector3(0.29f, 1.16f, 0f), new Vector3(0.16f, 0.14f, 0.2f), true, false);
        AddAvatarPart(av, PrimitiveType.Cube, "ArmL", new Vector3(-0.32f, 0.82f, 0f), new Vector3(0.14f, 0.56f, 0.14f), true, false);
        AddAvatarPart(av, PrimitiveType.Cube, "ArmR", new Vector3(0.32f, 0.82f, 0f), new Vector3(0.14f, 0.56f, 0.14f), true, false);
        AddAvatarPart(av, PrimitiveType.Cube, "HandL", new Vector3(-0.32f, 0.5f, 0f), new Vector3(0.13f, 0.12f, 0.13f), false, false);
        AddAvatarPart(av, PrimitiveType.Cube, "HandR", new Vector3(0.32f, 0.5f, 0f), new Vector3(0.13f, 0.12f, 0.13f), false, false);
        AddAvatarPart(av, PrimitiveType.Cube, "LegL", new Vector3(-0.13f, 0.4f, 0f), new Vector3(0.16f, 0.56f, 0.16f), false, true);
        AddAvatarPart(av, PrimitiveType.Cube, "LegR", new Vector3(0.13f, 0.4f, 0f), new Vector3(0.16f, 0.56f, 0.16f), false, true);
        AddAvatarPart(av, PrimitiveType.Cube, "Neck", new Vector3(0f, 1.24f, 0f), new Vector3(0.12f, 0.1f, 0.12f), false, false);
        AddAvatarPart(av, PrimitiveType.Cube, "Head", new Vector3(0f, 1.36f, 0f), new Vector3(0.32f, 0.32f, 0.32f), false, false);
        AddAvatarPart(av, PrimitiveType.Cube, "Helmet", new Vector3(0f, 1.56f, 0f), new Vector3(0.4f, 0.1f, 0.4f), true, false);
        AddAvatarPart(av, PrimitiveType.Cube, "Vest", new Vector3(0f, 0.92f, -0.19f), new Vector3(0.36f, 0.6f, 0.05f), true, false);

        ApplyAvatarOutfit(av, coat, trouser);

        GameObject nl = new GameObject("Name");
        nl.transform.SetParent(root.transform, false);
        nl.transform.localPosition = new Vector3(0f, 2.15f, 0f);
        TextMesh tm = nl.AddComponent<TextMesh>();
        tm.font = UiFont;
        tm.fontSize = 48;
        tm.characterSize = 0.09f;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = Color.white;
        tm.text = name;
        if (tm.font != null)
        {
            nl.GetComponent<Renderer>().sharedMaterial = GetLabelMaterial(Color.white);
        }
        av.nameLabel = tm;
        return av;
    }

    private void SetAvatarLimb(RemoteAvatar av, string limbName, Quaternion rot)
    {
        if (av.root == null)
        {
            return;
        }
        Transform t = av.root.transform.Find(limbName);
        if (t != null)
        {
            t.localRotation = rot;
        }
    }

    // 刷新远程玩家衣服颜色（换装同步）
    private void ApplyAvatarOutfit(RemoteAvatar av, int coat, int trouser)
    {
        av.coat = coat;
        av.trouser = trouser;
        Color coatC = coat >= 0 && coat < CoatPalette.Length ? CoatPalette[coat] : CoatPalette[0];
        Color trouserC = trouser >= 0 && trouser < TrouserPalette.Length ? TrouserPalette[trouser] : TrouserPalette[0];
        for (int i = 0; i < av.coatRenderers.Count; i++)
        {
            if (av.coatRenderers[i] != null)
            {
                av.coatRenderers[i].sharedMaterial = SimpleMaterial(coatC);
            }
        }
        for (int i = 0; i < av.trouserRenderers.Count; i++)
        {
            if (av.trouserRenderers[i] != null)
            {
                av.trouserRenderers[i].sharedMaterial = SimpleMaterial(trouserC);
            }
        }
    }

    private void SendChat()
    {
        if (!inRoom || string.IsNullOrEmpty(chatInput.Trim()))
        {
            return;
        }
        PlayUiClick();
        string text = chatInput.Trim();
        chatInput = "";
        StartCoroutine(SendChatRoutine(text));
    }

    private System.Collections.IEnumerator SendChatRoutine(string text)
    {
        string body = "{\"room_id\":\"" + roomId + "\",\"username\":\"" + JsonEscape(currentAccount)
            + "\",\"display_name\":\"" + JsonEscape(string.IsNullOrEmpty(displayName) ? currentAccount : displayName)
            + "\",\"text\":\"" + JsonEscape(text) + "\"}";
        yield return SupabaseRequest("POST", "/rest/v1/messages", body, null);
    }

    private void PollChat()
    {
        if (!inRoom)
        {
            return;
        }
        StartCoroutine(PollChatRoutine());
    }

    private System.Collections.IEnumerator PollChatRoutine()
    {
        string path = "/rest/v1/messages?room_id=eq." + roomId + "&order=id.asc&id=gt." + lastChatId + "&select=id,username,display_name,text&limit=50";
        string result = null;
        yield return SupabaseRequest("GET", path, null, (r) => result = r);
        if (string.IsNullOrEmpty(result) || result == "[]")
        {
            yield break;
        }
        MessageRows rows = JsonUtility.FromJson<MessageRows>("{\"items\":" + result + "}");
        if (rows == null || rows.items == null)
        {
            yield break;
        }
        for (int i = 0; i < rows.items.Length; i++)
        {
            MessageRow m = rows.items[i];
            chatMessages.Add(m.display_name + "：" + m.text);
            if (m.id > lastChatId)
            {
                lastChatId = m.id;
            }
            if (chatMessages.Count > 40)
            {
                chatMessages.RemoveAt(0);
            }
        }
    }

    // ── 联机共享工单（房主上传、其他玩家拉取并合并）──────
    [System.Serializable] private class OrderArray { public OrderSaveData[] items; }
    [System.Serializable] private class RoomStateRow { public OrderArray orders; }
    [System.Serializable] private class RoomStateRows { public RoomStateRow[] items; }

    private string SerializeOrdersJson()
    {
        OrderSaveData[] arr = new OrderSaveData[orders.Count];
        for (int i = 0; i < orders.Count; i++)
        {
            Order o = orders[i];
            arr[i] = new OrderSaveData
            {
                id = o.id, title = o.title, room = o.room, cause = o.cause, plan = o.plan, cost = o.cost,
                siteX = o.site.x, siteY = o.site.y, siteZ = o.site.z, state = (int)o.state,
                repairProgress = o.repairProgress, schedDay = o.schedDay, schedHour = o.schedHour,
                tool = (int)o.requiredTool, sensorName = o.sensorName, sensorUnit = o.sensorUnit,
                sensorValue = o.sensorValue, sensorNormal = o.sensorNormal, sensorAlarm = o.sensorAlarm,
                sensorMax = o.sensorMax, alarmTime = o.alarmTime, fixTime = o.fixTime, normalSince = o.normalSince, verified = o.verified,
                selfRepairable = o.selfRepairable
            };
        }
        return JsonUtility.ToJson(new OrderArray { items = arr });
    }

    private void ApplyRemoteOrders(OrderSaveData[] remote)
    {
        if (remote == null)
        {
            return;
        }
        Dictionary<int, Order> local = new Dictionary<int, Order>();
        for (int i = 0; i < orders.Count; i++)
        {
            local[orders[i].id] = orders[i];
        }
        for (int i = 0; i < remote.Length; i++)
        {
            OrderSaveData sd = remote[i];
            Order o;
            if (local.TryGetValue(sd.id, out o))
            {
                // 合并策略：取「更靠前」的状态，避免别人的旧状态把自己的进度冲掉
                bool remoteAdvanced = (int)sd.state > (int)o.state
                    || (sd.state == (int)o.state && sd.repairProgress > o.repairProgress);
                if (remoteAdvanced)
                {
                    o.state = (OrderState)sd.state;
                    o.repairProgress = sd.repairProgress;
                    o.fixTime = sd.fixTime;
                    o.normalSince = sd.normalSince;
                    o.verified = o.verified || sd.verified;
                    o.needsRebuild = true;
                }
                else if (sd.verified && !o.verified)
                {
                    o.verified = true;
                    o.needsRebuild = true;
                }
            }
            else
            {
                o = new Order
                {
                    id = sd.id, title = sd.title, room = sd.room, cause = sd.cause, plan = sd.plan, cost = sd.cost,
                    site = new Vector3(sd.siteX, sd.siteY, sd.siteZ), state = (OrderState)sd.state,
                    repairProgress = sd.repairProgress, schedDay = sd.schedDay, schedHour = sd.schedHour,
                    requiredTool = (ToolKind)sd.tool, sensorName = sd.sensorName, sensorUnit = sd.sensorUnit,
                    sensorValue = sd.sensorValue, sensorNormal = sd.sensorNormal, sensorAlarm = sd.sensorAlarm,
                    sensorMax = sd.sensorMax, alarmTime = sd.alarmTime, fixTime = sd.fixTime, normalSince = sd.normalSince, verified = sd.verified,
                    selfRepairable = sd.selfRepairable
                };
                BuildOrderMarker(o);
                orders.Add(o);
            }
        }
    }

    private void SyncRoomOrders()
    {
        if (!inRoom || !loggedIn)
        {
            return;
        }
        // 先拉取合并，若本地进度更靠前再上传（避免旧状态覆盖新状态）
        StartCoroutine(SyncRoomOrdersRoutine());
    }

    private System.Collections.IEnumerator SyncRoomOrdersRoutine()
    {
        string path = "/rest/v1/room_state?room_id=eq." + roomId + "&select=orders";
        string result = null;
        yield return SupabaseRequest("GET", path, null, (r) => result = r);
        if (result == null)
        {
            yield break;
        }
        if (result != "[]")
        {
            RoomStateRows rows = JsonUtility.FromJson<RoomStateRows>("{\"items\":" + result + "}");
            if (rows != null && rows.items != null && rows.items.Length > 0 && rows.items[0].orders != null)
            {
                ApplyRemoteOrders(rows.items[0].orders.items);
            }
        }
        // 本地进度若有推进，推送到云端
        int sig = LocalProgressSignature();
        if (sig != lastUploadedSignature)
        {
            yield return UploadRoomOrders();
            lastUploadedSignature = sig;
        }
    }

    // 本地工单进度指纹：状态 + 维修进度 + 验收
    private int LocalProgressSignature()
    {
        int sig = 0;
        for (int i = 0; i < orders.Count; i++)
        {
            Order o = orders[i];
            sig += o.id * 7919 + (int)o.state * 1000 + Mathf.RoundToInt(o.repairProgress * 100f)
                + (o.verified ? 500000 : 0);
        }
        return sig + orders.Count;
    }

    private System.Collections.IEnumerator UploadRoomOrders()
    {
        string body = "{\"room_id\":\"" + roomId + "\",\"orders\":" + SerializeOrdersJson() + "}";
        yield return SupabaseRequest("POST", "/rest/v1/room_state", body, null, "resolution=merge-duplicates");
    }

    private System.Collections.IEnumerator DownloadRoomOrders()
    {
        string path = "/rest/v1/room_state?room_id=eq." + roomId + "&select=orders";
        string result = null;
        yield return SupabaseRequest("GET", path, null, (r) => result = r);
        if (result == null || result == "[]")
        {
            yield break;
        }
        RoomStateRows rows = JsonUtility.FromJson<RoomStateRows>("{\"items\":" + result + "}");
        if (rows == null || rows.items == null || rows.items.Length == 0 || rows.items[0].orders == null)
        {
            yield break;
        }
        ApplyRemoteOrders(rows.items[0].orders.items);
    }

    // ── 存档数据（JsonUtility 序列化）────────────────────
    [System.Serializable]
    private class SaveData
    {
        public int version = 1;
        public int income;
        public int expenses;
        public float gameTime;
        public int orderSerial;
        public float playerX;
        public float playerZ;
        public int guideStep;
        public int[] unlockedTools;
        public OrderSaveData[] orders;
    }

    [System.Serializable]
    private class OrderSaveData
    {
        public int id;
        public string title;
        public string room;
        public string cause;
        public string plan;
        public int cost;
        public float siteX, siteY, siteZ;
        public int state;
        public float repairProgress;
        public int schedDay;
        public int schedHour;
        public int tool;
        public string sensorName;
        public string sensorUnit;
        public float sensorValue;
        public float sensorNormal;
        public float sensorAlarm;
        public float sensorMax;
        public float alarmTime;
        public float fixTime;
        public float normalSince;
        public bool verified;
        public bool selfRepairable;
    }

    // ── 存档（按账户保存经营数据 + 工单 + 位置）─────────
    private void SaveGame()
    {
        if (string.IsNullOrEmpty(currentAccount) || currentAccount == "游客")
        {
            return;
        }
        SaveData data = new SaveData
        {
            income = income,
            expenses = expenses,
            gameTime = gameTime,
            orderSerial = orderSerial,
            playerX = playerPosition.x,
            playerZ = playerPosition.z,
            guideStep = guideStep,
            orders = new OrderSaveData[orders.Count]
        };
        for (int i = 0; i < orders.Count; i++)
        {
            Order o = orders[i];
            data.orders[i] = new OrderSaveData
            {
                id = o.id,
                title = o.title,
                room = o.room,
                cause = o.cause,
                plan = o.plan,
                cost = o.cost,
                siteX = o.site.x, siteY = o.site.y, siteZ = o.site.z,
                state = (int)o.state,
                repairProgress = o.repairProgress,
                schedDay = o.schedDay,
                schedHour = o.schedHour,
                tool = (int)o.requiredTool,
                sensorName = o.sensorName,
                sensorUnit = o.sensorUnit,
                sensorValue = o.sensorValue,
                sensorNormal = o.sensorNormal,
                sensorAlarm = o.sensorAlarm,
                sensorMax = o.sensorMax,
                alarmTime = o.alarmTime,
                fixTime = o.fixTime,
                normalSince = o.normalSince,
                verified = o.verified,
                selfRepairable = o.selfRepairable
            };
        }
        // 已解锁工具（kind 枚举值）
        List<int> unlockedKinds = new List<int>();
        for (int i = 0; i < tools.Count; i++)
        {
            if (tools[i].unlocked)
            {
                unlockedKinds.Add((int)tools[i].kind);
            }
        }
        data.unlockedTools = unlockedKinds.ToArray();
        string key = "kitchen_save_" + currentAccount.ToLowerInvariant();
        string json = JsonUtility.ToJson(data);
        PlayerPrefs.SetString(key, json);
        PlayerPrefs.Save();
        // 云端存档（Supabase，异步不阻塞）
        if (CloudEnabled)
        {
            StartCoroutine(SaveToCloud(currentAccount, json));
        }
    }

    private void LoadGame()
    {
        if (string.IsNullOrEmpty(currentAccount) || currentAccount == "游客")
        {
            return;
        }
        string key = "kitchen_save_" + currentAccount.ToLowerInvariant();
        if (!PlayerPrefs.HasKey(key))
        {
            return;
        }
        SaveData data = JsonUtility.FromJson<SaveData>(PlayerPrefs.GetString(key));
        if (data == null)
        {
            return;
        }

        ApplySaveData(data);
        ShowToast("已载入本地存档：累计收入 ¥" + income.ToString("N0") + "，成本 ¥" + expenses.ToString("N0")
            + "，工单 " + orders.Count + " 单", 5f);
    }

    // 把存档数据恢复到游戏状态（本地/云端共用）
    private void ApplySaveData(SaveData data)
    {
        if (data == null)
        {
            return;
        }
        income = data.income;
        expenses = data.expenses;
        if (data.gameTime > 0f)
        {
            gameTime = data.gameTime;
            lastDay = DayIndex;
            lastPaidMonth = GameMonth;
        }
        if (data.orderSerial > orderSerial)
        {
            orderSerial = data.orderSerial;
        }
        playerPosition = new Vector3(data.playerX, GroundLevel, data.playerZ);
        if (player != null)
        {
            player.transform.position = playerPosition;
        }
        guideStep = data.guideStep;

        // 恢复已解锁工具
        if (data.unlockedTools != null)
        {
            for (int i = 0; i < tools.Count; i++)
            {
                tools[i].unlocked = tools[i].kind == ToolKind.Wrench;
            }
            for (int j = 0; j < data.unlockedTools.Length; j++)
            {
                ToolKind kind = (ToolKind)data.unlockedTools[j];
                for (int i = 0; i < tools.Count; i++)
                {
                    if (tools[i].kind == kind)
                    {
                        tools[i].unlocked = true;
                    }
                }
            }
        }

        // 恢复工单（登录时列表为空，直接重建）
        if (data.orders != null)
        {
            for (int i = 0; i < data.orders.Length; i++)
            {
                OrderSaveData sd = data.orders[i];
                Order o = new Order
                {
                    id = sd.id,
                    title = sd.title,
                    room = sd.room,
                    cause = sd.cause,
                    plan = sd.plan,
                    cost = sd.cost,
                    site = new Vector3(sd.siteX, sd.siteY, sd.siteZ),
                    state = (OrderState)sd.state,
                    repairProgress = sd.repairProgress,
                    schedDay = sd.schedDay,
                    schedHour = sd.schedHour,
                    requiredTool = (ToolKind)sd.tool,
                    sensorName = sd.sensorName,
                    sensorUnit = sd.sensorUnit,
                    sensorValue = sd.sensorValue,
                    sensorNormal = sd.sensorNormal,
                    sensorAlarm = sd.sensorAlarm,
                    sensorMax = sd.sensorMax,
                    alarmTime = sd.alarmTime,
                    fixTime = sd.fixTime,
                    normalSince = sd.normalSince,
                    verified = sd.verified,
                    selfRepairable = sd.selfRepairable
                };
                BuildOrderMarker(o);
                orders.Add(o);
            }
        }
    }

    private void EnterGame(string accountName)
    {
        currentAccount = accountName;
        loggedIn = true;
        if (accountName != "游客")
        {
            AccountStore.UpdateAppearance(accountName, custCoat, custTrouser);
            if (CloudEnabled)
            {
                StartCoroutine(LoadFromCloud(accountName));   // 云端存档，失败自动回退本地
            }
            else
            {
                LoadGame();
            }
        }
        else
        {
            LoadGame();   // 游客用本地存档
        }
        string shown = string.IsNullOrEmpty(displayName) ? accountName : displayName;
        if (playerNameLabel != null)
        {
            playerNameLabel.text = shown;
        }
        SetCursorLock(true);
        ShowToast("欢迎，" + shown + "　·　按 T 打开数字孪生监测平台", 6f);
    }

    private void TryLogin()
    {
        if (authBusy)
        {
            return;
        }
        if (string.IsNullOrEmpty(loginUser))
        {
            loginMessage = "请输入用户名";
            return;
        }
        if (!CloudEnabled)
        {
            string name;
            int coat, trouser;
            if (AccountStore.TryLoad(loginUser, loginPass, out name, out coat, out trouser))
            {
                ApplyAppearance(coat, trouser);
                displayName = name;
                EnterGame(loginUser);
            }
            else
            {
                loginMessage = AccountStore.Exists(loginUser) ? "密码不正确" : "该用户名不存在，请先注册";
            }
            return;
        }
        authBusy = true;
        loginMessage = "登录中…";
        StartCoroutine(LoginRoutine(loginUser, loginPass));
    }

    private void TryRegister()
    {
        if (authBusy)
        {
            return;
        }
        if (string.IsNullOrEmpty(loginUser) || loginPass.Length < 3)
        {
            loginMessage = "用户名不能为空，密码至少 3 位";
            return;
        }
        if (!CloudEnabled)
        {
            if (AccountStore.Exists(loginUser))
            {
                loginMessage = "该用户名已被注册";
                return;
            }
            AccountStore.Save(loginUser, loginPass, loginName, custCoat, custTrouser);
            displayName = loginName;
            loginMessage = "注册成功，已自动登录";
            EnterGame(loginUser);
            return;
        }
        authBusy = true;
        loginMessage = "注册中…";
        StartCoroutine(RegisterRoutine(loginUser, loginPass));
    }

    private Rect LobbyRect { get { return new Rect((Screen.width - 360f) * 0.5f, (Screen.height - 300f) * 0.5f, 360f, 300f); } }
    private Rect ChatRect { get { return new Rect(16f, Screen.height - 216f, 320f, 200f); } }

    private void DrawLobby()
    {
        if (!loggedIn || !lobbyOpen)
        {
            return;
        }
        float width = 360f;
        float height = 300f;
        Rect rect = LobbyRect;
        DrawPanel(rect, new Color(0.06f, 0.08f, 0.11f, 0.98f), new Color(1f, 1f, 1f, 0.18f));
        GUI.Label(new Rect(rect.x + 20f, rect.y + 14f, width - 108f, 26f), "联机大厅（最多 4 人）", titleStyle);

        if (inRoom)
        {
            GUI.Label(new Rect(rect.x + 20f, rect.y + 52f, width - 40f, 22f), "当前房间号：" + roomCode + "（告诉队友这个号）", bodyStyle);
            GUI.Label(new Rect(rect.x + 20f, rect.y + 82f, width - 40f, 22f), "在线人数：" + (remotePlayers.Count + 1) + " / 4", bodyStyle);
            Rect leave = new Rect(rect.x + 20f, rect.y + 116f, width - 40f, 38f);
            DrawPanel(leave, new Color(0.55f, 0.2f, 0.2f, 0.95f), Color.clear);
            if (GUI.Button(leave, GUIContent.none, GUIStyle.none))
            {
                LeaveRoom();
            }
            GUI.Label(leave, "离开房间", buttonStyle);
        }
        else
        {
            Rect create = new Rect(rect.x + 20f, rect.y + 52f, width - 40f, 44f);
            DrawPanel(create, btnBlue, Color.clear);
            if (GUI.Button(create, GUIContent.none, GUIStyle.none))
            {
                CreateRoom();
            }
            GUI.Label(create, "创建房间", buttonStyle);

            GUI.Label(new Rect(rect.x + 20f, rect.y + 108f, width - 40f, 20f), "输入房间号加入（回车确认）：", bodyStyle);
            GUI.SetNextControlName("joinCodeField");
            joinCode = GUI.TextField(new Rect(rect.x + 20f, rect.y + 130f, width - 130f, 30f), joinCode, 8);
            Rect join = new Rect(rect.x + width - 100f, rect.y + 130f, 80f, 30f);
            DrawPanel(join, btnBlue, Color.clear);
            bool joinClicked = GUI.Button(join, GUIContent.none, GUIStyle.none);
            bool joinEnter = Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                && GUI.GetNameOfFocusedControl() == "joinCodeField";
            if (joinClicked || joinEnter)
            {
                JoinRoom();
            }
            GUI.Label(join, "加入", cardButtonStyle);
        }

        // 关闭按钮（不依赖键盘，避免输入框吃掉按键）
        Rect close = new Rect(rect.x + width - 84f, rect.y + 12f, 68f, 26f);
        DrawPanel(close, new Color(0.3f, 0.32f, 0.36f, 0.95f), Color.clear);
        if (GUI.Button(close, GUIContent.none, GUIStyle.none))
        {
            lobbyOpen = false;
            SetCursorLock(true);
        }
        GUI.Label(close, "关闭", cardButtonStyle);

        GUI.Label(new Rect(rect.x + 20f, rect.y + height - 44f, width - 40f, 20f), roomMessage, smallStyle);
    }

    private void DrawChat()
    {
        if (!loggedIn || !inRoom)
        {
            return;
        }
        float width = 320f;
        float height = 200f;
        Rect rect = new Rect(16f, Screen.height - height - 16f, width, height);
        DrawPanel(new Rect(rect.x, rect.y, width, height - 34f), new Color(0.04f, 0.06f, 0.09f, 0.75f), Color.clear);
        float y = rect.y + height - 42f;
        for (int i = chatMessages.Count - 1; i >= 0; i--)
        {
            GUI.Label(new Rect(rect.x + 8f, y, width - 16f, 18f), chatMessages[i], smallStyle);
            y -= 18f;
            if (y < rect.y + 6f)
            {
                break;
            }
        }
        Rect inputRect = new Rect(rect.x, rect.y + height - 30f, width - 62f, 28f);
        GUI.SetNextControlName("chatInput");
        chatInput = GUI.TextField(inputRect, chatInput, 40);
        bool enterPressed = Event.current.type == EventType.KeyDown
            && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
            && GUI.GetNameOfFocusedControl() == "chatInput";
        Rect send = new Rect(rect.x + width - 58f, rect.y + height - 30f, 54f, 28f);
        DrawPanel(send, btnBlue, Color.clear);
        if (GUI.Button(send, GUIContent.none, GUIStyle.none) || enterPressed)
        {
            SendChat();
        }
        GUI.Label(send, "发送", cardButtonStyle);
    }

    private void DrawLogin()
    {
        if (loggedIn)
        {
            return;
        }

        Fill(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0.04f, 0.06f, 0.09f, 0.82f));

        float width = 420f;
        float height = 330f;
        Rect rect = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
        DrawPanel(rect, new Color(0.06f, 0.08f, 0.11f, 0.98f), new Color(1f, 1f, 1f, 0.18f));

        GUI.Label(new Rect(rect.x + 24f, rect.y + 18f, width - 48f, 30f), "焕新家装 · 员工登录", titleStyle);
        GUI.Label(new Rect(rect.x + 24f, rect.y + 46f, width - 48f, 20f), "云端账户（Supabase）　·　未登录无法开工", smallStyle);

        string[] tabs = { "登录", "注册", "外观" };
        float tabW = (width - 48f - 16f) / 3f;
        for (int i = 0; i < tabs.Length; i++)
        {
            Rect tab = new Rect(rect.x + 24f + i * (tabW + 8f), rect.y + 74f, tabW, 30f);
            Fill(tab, i == loginTab ? btnBlue : new Color(1f, 1f, 1f, 0.07f));
            if (GUI.Button(tab, GUIContent.none, GUIStyle.none))
            {
                loginTab = i;
                loginMessage = string.Empty;
            }
            GUI.Label(tab, tabs[i], cardButtonStyle);
        }

        float y = rect.y + 118f;
        if (loginTab != 2)
        {
            bool isRegister = loginTab == 1;
            float passY = y + (isRegister ? 68f : 34f);
            float actionY = y + (isRegister ? 110f : 76f);

            GUI.Label(new Rect(rect.x + 24f, y, 70f, 24f), "用户名", bodyStyle);
            loginUser = GUI.TextField(new Rect(rect.x + 96f, y - 2f, width - 130f, 28f), loginUser, 16);

            if (isRegister)
            {
                GUI.Label(new Rect(rect.x + 24f, y + 34f, 70f, 24f), "姓名", bodyStyle);
                loginName = GUI.TextField(new Rect(rect.x + 96f, y + 32f, width - 130f, 28f), loginName, 12);
            }

            loginPass = GUI.PasswordField(new Rect(rect.x + 96f, passY - 2f, width - 130f, 28f), loginPass, '*', 16);
            GUI.Label(new Rect(rect.x + 24f, passY + 2f, 70f, 24f), "密码", bodyStyle);

            Rect action = new Rect(rect.x + 24f, actionY, width - 48f, 40f);
            DrawPanel(action, btnBlue, Color.clear);
            if (GUI.Button(action, GUIContent.none, GUIStyle.none))
            {
                if (loginTab == 0)
                {
                    TryLogin();
                }
                else
                {
                    TryRegister();
                }
            }
            GUI.Label(action, loginTab == 0 ? "登 录" : "注 册", buttonStyle);
        }
        else
        {
            DrawAppearancePicker(rect, y);
        }

        GUI.Label(new Rect(rect.x + 24f, rect.y + height - 74f, width - 48f, 20f), loginMessage, smallStyle);

        Rect guest = new Rect(rect.x + 24f, rect.y + height - 50f, width - 48f, 34f);
        DrawPanel(guest, new Color(0.22f, 0.24f, 0.28f, 0.95f), Color.clear);
        if (GUI.Button(guest, GUIContent.none, GUIStyle.none))
        {
            ApplyAppearance(custCoat, custTrouser);
            EnterGame("游客");
        }
        GUI.Label(guest, "以游客身份进入（不保存进度）", cardButtonStyle);
    }

    private void DrawAppearancePicker(Rect rect, float y)
    {
        // 上衣
        GUI.Label(new Rect(rect.x + 24f, y - 4f, 200f, 22f), "上衣颜色", bodyStyle);
        Rect coatLeft = new Rect(rect.x + 24f, y + 20f, 34f, 30f);
        Rect coatRight = new Rect(rect.x + rect.width - 58f, y + 20f, 34f, 30f);
        Rect coatSwatch = new Rect(rect.x + 66f, y + 20f, rect.width - 132f, 30f);
        if (ArrowButton(coatLeft, "<"))
        {
            custCoat = (custCoat - 1 + CoatPalette.Length) % CoatPalette.Length;
            ApplyAppearance(custCoat, custTrouser);
        }
        if (ArrowButton(coatRight, ">"))
        {
            custCoat = (custCoat + 1) % CoatPalette.Length;
            ApplyAppearance(custCoat, custTrouser);
        }
        Fill(coatSwatch, CoatPalette[custCoat]);
        GUI.Label(coatSwatch, CoatNames[custCoat], cardButtonStyle);

        // 裤子
        float y2 = y + 62f;
        GUI.Label(new Rect(rect.x + 24f, y2 - 4f, 200f, 22f), "裤子颜色", bodyStyle);
        Rect legLeft = new Rect(rect.x + 24f, y2 + 20f, 34f, 30f);
        Rect legRight = new Rect(rect.x + rect.width - 58f, y2 + 20f, 34f, 30f);
        Rect legSwatch = new Rect(rect.x + 66f, y2 + 20f, rect.width - 132f, 30f);
        if (ArrowButton(legLeft, "<"))
        {
            custTrouser = (custTrouser - 1 + TrouserPalette.Length) % TrouserPalette.Length;
            ApplyAppearance(custCoat, custTrouser);
        }
        if (ArrowButton(legRight, ">"))
        {
            custTrouser = (custTrouser + 1) % TrouserPalette.Length;
            ApplyAppearance(custCoat, custTrouser);
        }
        Fill(legSwatch, TrouserPalette[custTrouser]);
        GUI.Label(legSwatch, TrouserNames[custTrouser], cardButtonStyle);
    }

    private bool ArrowButton(Rect rect, string label)
    {
        bool hover = rect.Contains(Event.current.mousePosition);
        Fill(rect, hover ? Color.Lerp(btnBlue, Color.white, 0.2f) : btnBlue);
        bool clicked = GUI.Button(rect, GUIContent.none, GUIStyle.none);
        GUI.Label(rect, label, cardButtonStyle);
        return clicked;
    }

    // ── 玩家 ──────────────────────────────────────────────
    private void BuildPlayer()
    {
        player = new GameObject("Inspector");
        playerPosition = spawnPosition;
        player.transform.position = spawnPosition;
        player.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
        lookYaw = 180f;

        // 上衣与裤子用独立材质，便于服装店换装
        playerClothMaterial = MakeMaterial(new Color(0.16f, 0.34f, 0.48f), 0.05f, 0.35f);
        playerTrouserMaterial = MakeMaterial(new Color(0.22f, 0.26f, 0.3f), 0.05f, 0.3f);
        Material bodyMaterial = playerClothMaterial;
        Material legMaterial = playerTrouserMaterial;

        // 身体与四肢在世界中可见（低头能看见），头部隐藏避免遮挡视线
        GameObject torso = MakePrimitive(PrimitiveType.Cube, "Torso", player.transform, new Vector3(0f, 0.85f, 0f), new Vector3(0.5f, 0.7f, 0.3f), Quaternion.identity, bodyMaterial);
        playerBody = torso.transform;
        playerClothRenderers.Add(torso.GetComponent<Renderer>());

        leftArmPivot = new GameObject("Left Arm Pivot").transform;
        leftArmPivot.SetParent(player.transform, false);
        leftArmPivot.localPosition = new Vector3(-0.32f, 1.1f, 0f);
        playerClothRenderers.Add(MakePrimitive(PrimitiveType.Cube, "Left Arm", leftArmPivot, new Vector3(0f, -0.28f, 0f), new Vector3(0.14f, 0.56f, 0.14f), Quaternion.identity, bodyMaterial).GetComponent<Renderer>());
        rightArmPivot = new GameObject("Right Arm Pivot").transform;
        rightArmPivot.SetParent(player.transform, false);
        rightArmPivot.localPosition = new Vector3(0.32f, 1.1f, 0f);
        playerClothRenderers.Add(MakePrimitive(PrimitiveType.Cube, "Right Arm", rightArmPivot, new Vector3(0f, -0.28f, 0f), new Vector3(0.14f, 0.56f, 0.14f), Quaternion.identity, bodyMaterial).GetComponent<Renderer>());

        leftLegPivot = new GameObject("Left Leg Pivot").transform;
        leftLegPivot.SetParent(player.transform, false);
        leftLegPivot.localPosition = new Vector3(-0.13f, 0.68f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Left Leg", leftLegPivot, new Vector3(0f, -0.28f, 0f), new Vector3(0.16f, 0.56f, 0.16f), Quaternion.identity, legMaterial);
        rightLegPivot = new GameObject("Right Leg Pivot").transform;
        rightLegPivot.SetParent(player.transform, false);
        rightLegPivot.localPosition = new Vector3(0.13f, 0.68f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Right Leg", rightLegPivot, new Vector3(0f, -0.28f, 0f), new Vector3(0.16f, 0.56f, 0.16f), Quaternion.identity, legMaterial);

        // 头部（第一人称隐藏、第三人称显示）
        GameObject head = MakePrimitive(PrimitiveType.Cube, "Player Head", player.transform, new Vector3(0f, 1.36f, 0f), new Vector3(0.32f, 0.32f, 0.32f), Quaternion.identity, MakeMaterial(new Color(0.84f, 0.66f, 0.5f), 0.02f, 0.3f));
        GameObject helmet = MakePrimitive(PrimitiveType.Cube, "Player Helmet", player.transform, new Vector3(0f, 1.56f, 0f), new Vector3(0.4f, 0.1f, 0.4f), Quaternion.identity, MakeMaterial(new Color(0.95f, 0.72f, 0.12f), 0.1f, 0.45f));
        GameObject vest = MakePrimitive(PrimitiveType.Cube, "Player Vest", player.transform, new Vector3(0f, 0.92f, -0.19f), new Vector3(0.36f, 0.6f, 0.05f), Quaternion.identity, MakeMaterial(new Color(0.95f, 0.6f, 0.15f), 0.05f, 0.4f));
        GameObject waist = MakePrimitive(PrimitiveType.Cube, "Player Waist", player.transform, new Vector3(0f, 0.68f, 0f), new Vector3(0.4f, 0.3f, 0.26f), Quaternion.identity, playerClothMaterial);
        MakePrimitive(PrimitiveType.Cube, "Player Neck", player.transform, new Vector3(0f, 1.24f, 0f), new Vector3(0.12f, 0.1f, 0.12f), Quaternion.identity, MakeMaterial(new Color(0.84f, 0.66f, 0.5f), 0.02f, 0.3f));
        MakePrimitive(PrimitiveType.Cube, "Player Shoulder L", player.transform, new Vector3(-0.29f, 1.16f, 0f), new Vector3(0.16f, 0.14f, 0.2f), Quaternion.identity, playerClothMaterial);
        MakePrimitive(PrimitiveType.Cube, "Player Shoulder R", player.transform, new Vector3(0.29f, 1.16f, 0f), new Vector3(0.16f, 0.14f, 0.2f), Quaternion.identity, playerClothMaterial);
        MakePrimitive(PrimitiveType.Cube, "Player Hand L", leftArmPivot, new Vector3(0f, -0.6f, 0f), new Vector3(0.13f, 0.12f, 0.13f), Quaternion.identity, MakeMaterial(new Color(0.84f, 0.66f, 0.5f), 0.02f, 0.3f));
        MakePrimitive(PrimitiveType.Cube, "Player Hand R", rightArmPivot, new Vector3(0f, -0.6f, 0f), new Vector3(0.13f, 0.12f, 0.13f), Quaternion.identity, MakeMaterial(new Color(0.84f, 0.66f, 0.5f), 0.02f, 0.3f));
        playerClothRenderers.Add(waist.GetComponent<Renderer>());
        playerHead = head.transform;
        playerHeadRenderers = new[]
        {
            head.GetComponent<Renderer>(),
            helmet.GetComponent<Renderer>(),
            vest.GetComponent<Renderer>(),
            torso.GetComponent<Renderer>()
        };
        SetHeadVisible(false);

        // 头顶姓名标签（跟随玩家，第三人称可见）
        GameObject nameObj = new GameObject("Player Name");
        nameObj.transform.SetParent(player.transform, false);
        nameObj.transform.localPosition = new Vector3(0f, 1.85f, 0f);
        nameObj.transform.localRotation = Quaternion.identity;
        playerNameLabel = nameObj.AddComponent<TextMesh>();
        playerNameLabel.font = UiFont;
        playerNameLabel.fontSize = 48;
        playerNameLabel.characterSize = 0.09f;
        playerNameLabel.anchor = TextAnchor.MiddleCenter;
        playerNameLabel.alignment = TextAlignment.Center;
        playerNameLabel.color = Color.white;
        Renderer nameRenderer = nameObj.GetComponent<Renderer>();
        if (playerNameLabel.font != null)
        {
            nameRenderer.sharedMaterial = GetLabelMaterial(Color.white);
        }

        BuildOutfits();
        BuildViewmodel();
    }

    // ── 服装 ──────────────────────────────────────────────
    private void BuildOutfits()
    {
        outfits.Add(new Outfit { name = "标准工装", price = 0, coat = new Color(0.16f, 0.34f, 0.48f), trouser = new Color(0.22f, 0.26f, 0.3f), owned = true });
        outfits.Add(new Outfit { name = "耐磨劳保服", price = 260, coat = new Color(0.45f, 0.36f, 0.2f), trouser = new Color(0.3f, 0.27f, 0.22f) });
        outfits.Add(new Outfit { name = "亮橙反光衣", price = 420, coat = new Color(0.92f, 0.45f, 0.12f), trouser = new Color(0.26f, 0.3f, 0.36f) });
        outfits.Add(new Outfit { name = "深灰技师服", price = 520, coat = new Color(0.24f, 0.26f, 0.3f), trouser = new Color(0.17f, 0.18f, 0.2f) });
        outfits.Add(new Outfit { name = "藏青监理装", price = 680, coat = new Color(0.12f, 0.2f, 0.4f), trouser = new Color(0.14f, 0.16f, 0.22f) });
        outfits.Add(new Outfit { name = "枣红工装夹克", price = 840, coat = new Color(0.5f, 0.18f, 0.18f), trouser = new Color(0.24f, 0.22f, 0.22f) });
        ApplyOutfit(0);
    }

    private void ApplyOutfit(int index)
    {
        if (index < 0 || index >= outfits.Count)
        {
            return;
        }
        currentOutfit = index;
        Outfit outfit = outfits[index];
        if (playerClothMaterial != null)
        {
            playerClothMaterial.color = outfit.coat;
        }
        if (playerTrouserMaterial != null)
        {
            playerTrouserMaterial.color = outfit.trouser;
        }
    }

    private void BuyOutfit(int index)
    {
        if (index < 0 || index >= outfits.Count || outfits[index].owned)
        {
            if (index >= 0 && index < outfits.Count)
            {
                ApplyOutfit(index);
                ShowToast("已换上「" + outfits[index].name + "」", 3f);
            }
            return;
        }
        Outfit outfit = outfits[index];
        if (Cash < outfit.price)
        {
            ShowToast("现金不足：「" + outfit.name + "」需 ¥" + outfit.price.ToString("N0")
                + "，当前 ¥" + Cash.ToString("N0") + "，还差 ¥" + (outfit.price - Cash).ToString("N0"), 4f);
            return;
        }
        AddExpense(outfit.price);
        outfit.owned = true;
        ApplyOutfit(index);
        ShowToast("购入并换上「" + outfit.name + "」（¥" + outfit.price.ToString("N0") + "）", 4f);
    }

    // ── 工具背包 ──────────────────────────────────────────
    private void BuildTools()
    {
        // 开局只有一把活动扳手，只能做"管件"类的头三单（水槽渗漏 / 燃气管 / 地漏）
        // 其余的活需要先赚钱再买工具，业务逐步拓展
        tools.Add(new ToolInfo("活动扳手", ToolKind.Wrench, new Color(0.6f, 0.62f, 0.66f), 0, true));
        tools.Add(new ToolInfo("防水胶布", ToolKind.Tape, new Color(0.15f, 0.15f, 0.16f), 90, false));
        tools.Add(new ToolInfo("剪刀", ToolKind.Scissors, new Color(0.75f, 0.76f, 0.8f), 120, false));
        tools.Add(new ToolInfo("螺丝刀", ToolKind.Screwdriver, new Color(0.85f, 0.72f, 0.1f), 180, false));
        tools.Add(new ToolInfo("羊角锤", ToolKind.Hammer, new Color(0.45f, 0.47f, 0.5f), 260, false));
        tools.Add(new ToolInfo("电动起子", ToolKind.Drill, new Color(0.85f, 0.35f, 0.12f), 300, false));
        tools.Add(new ToolInfo("测电笔", ToolKind.Tester, new Color(0.9f, 0.25f, 0.2f), 420, false));
        currentTool = 0;
        BuildToolModel();
    }

    private void NextTool(int delta)
    {
        if (tools.Count == 0)
        {
            return;
        }
        // 只在已拥有的工具之间循环
        for (int step = 0; step < tools.Count; step++)
        {
            currentTool = (currentTool + delta) % tools.Count;
            if (currentTool < 0)
            {
                currentTool += tools.Count;
            }
            if (tools[currentTool].unlocked)
            {
                break;
            }
        }
        if (!tools[currentTool].unlocked)
        {
            return;
        }
        BuildToolModel();
        ShowToast("切换工具：" + tools[currentTool].name, 2f);
    }

    private void BuyTool(int index)
    {
        if (index < 0 || index >= tools.Count)
        {
            return;
        }
        if (tools[index].unlocked)
        {
            ShowToast("已经拥有「" + tools[index].name + "」了", 2f);
            return;
        }
        ToolInfo tool = tools[index];
        if (Cash < tool.price)
        {
            ShowToast("现金不足：「" + tool.name + "」需 ¥" + tool.price.ToString("N0")
                + "，当前 ¥" + Cash.ToString("N0") + "，还差 ¥" + (tool.price - Cash).ToString("N0"), 4f);
            return;
        }
        AddExpense(tool.price);
        tool.unlocked = true;
        currentTool = index;
        BuildToolModel();
        ShowToast("已购入 " + tool.name + "（¥" + tool.price.ToString("N0") + "）", 4f);
    }

    // 按枚举取工具名，用于提示"该用哪把工具"
    private string ToolName(ToolKind kind)
    {
        for (int i = 0; i < tools.Count; i++)
        {
            if (tools[i].kind == kind)
            {
                return tools[i].name;
            }
        }
        return "合适工具";
    }

    private bool HasTool(ToolKind kind)
    {
        for (int i = 0; i < tools.Count; i++)
        {
            if (tools[i].kind == kind)
            {
                return tools[i].unlocked;
            }
        }
        return false;
    }

    private int UnlockedToolCount()
    {
        int count = 0;
        for (int i = 0; i < tools.Count; i++)
        {
            if (tools[i].unlocked)
            {
                count++;
            }
        }
        return count;
    }

    // 按当前工具重建手持模型
    private void BuildToolModel()
    {
        if (toolPivot == null || tools.Count == 0)
        {
            return;
        }

        for (int i = toolPivot.childCount - 1; i >= 0; i--)
        {
            Transform child = toolPivot.GetChild(i);
            child.SetParent(null, false);
            Destroy(child.gameObject);
        }

        ToolInfo tool = tools[currentTool];
        Material grip = MakeMaterial(new Color(0.24f, 0.26f, 0.3f), 0.15f, 0.45f);
        Material metal = MakeMaterial(new Color(0.78f, 0.8f, 0.84f), 0.85f, 0.8f);
        Material accent = MakeMaterial(tool.color, 0.35f, 0.55f);
        Material skin = MakeMaterial(new Color(0.82f, 0.64f, 0.47f), 0.02f, 0.3f);

        // 握持的手
        DecoPart(PrimitiveType.Cube, "Hand", toolPivot, new Vector3(0.01f, -0.06f, -0.02f), new Vector3(0.085f, 0.085f, 0.11f), Quaternion.identity, skin);

        switch (tool.kind)
        {
            case ToolKind.Drill:
                DecoPart(PrimitiveType.Cube, "Body", toolPivot, Vector3.zero, new Vector3(0.075f, 0.09f, 0.22f), Quaternion.identity, accent);
                DecoPart(PrimitiveType.Cube, "Grip", toolPivot, new Vector3(0f, -0.11f, -0.05f), new Vector3(0.06f, 0.14f, 0.07f), Quaternion.Euler(14f, 0f, 0f), grip);
                DecoPart(PrimitiveType.Cylinder, "Bit", toolPivot, new Vector3(0f, 0f, 0.16f), new Vector3(0.014f, 0.05f, 0.014f), Quaternion.Euler(90f, 0f, 0f), metal);
                break;

            case ToolKind.Screwdriver:
                DecoPart(PrimitiveType.Cylinder, "Shaft", toolPivot, new Vector3(0f, 0f, 0.11f), new Vector3(0.013f, 0.09f, 0.013f), Quaternion.Euler(90f, 0f, 0f), metal);
                DecoPart(PrimitiveType.Cylinder, "Ferrule", toolPivot, new Vector3(0f, 0f, 0.02f), new Vector3(0.022f, 0.02f, 0.022f), Quaternion.Euler(90f, 0f, 0f), metal);
                DecoPart(PrimitiveType.Cylinder, "Handle", toolPivot, new Vector3(0f, 0f, -0.05f), new Vector3(0.035f, 0.055f, 0.035f), Quaternion.Euler(90f, 0f, 0f), accent);
                break;

            case ToolKind.Wrench:
                DecoPart(PrimitiveType.Cube, "Handle", toolPivot, new Vector3(0f, 0f, -0.02f), new Vector3(0.035f, 0.028f, 0.2f), Quaternion.identity, metal);
                DecoPart(PrimitiveType.Cube, "Head", toolPivot, new Vector3(0f, 0f, 0.12f), new Vector3(0.09f, 0.03f, 0.08f), Quaternion.identity, metal);
                DecoPart(PrimitiveType.Cube, "Jaw Top", toolPivot, new Vector3(0.028f, 0.01f, 0.17f), new Vector3(0.03f, 0.03f, 0.05f), Quaternion.identity, metal);
                DecoPart(PrimitiveType.Cube, "Jaw Bottom", toolPivot, new Vector3(0.028f, -0.01f, 0.17f), new Vector3(0.03f, 0.02f, 0.05f), Quaternion.identity, metal);
                break;

            case ToolKind.Hammer:
                DecoPart(PrimitiveType.Cylinder, "Handle", toolPivot, new Vector3(0f, 0f, -0.03f), new Vector3(0.018f, 0.1f, 0.018f), Quaternion.Euler(90f, 0f, 0f), grip);
                DecoPart(PrimitiveType.Cube, "Head", toolPivot, new Vector3(0f, 0f, 0.1f), new Vector3(0.055f, 0.055f, 0.13f), Quaternion.identity, metal);
                DecoPart(PrimitiveType.Cube, "Claw", toolPivot, new Vector3(0f, 0f, 0.18f), new Vector3(0.04f, 0.05f, 0.05f), Quaternion.Euler(0f, 0f, 0f), metal);
                break;

            case ToolKind.Scissors:
                DecoPart(PrimitiveType.Cube, "Blade L", toolPivot, new Vector3(-0.014f, 0f, 0.1f), new Vector3(0.012f, 0.035f, 0.16f), Quaternion.Euler(0f, 6f, 0f), metal);
                DecoPart(PrimitiveType.Cube, "Blade R", toolPivot, new Vector3(0.014f, 0f, 0.1f), new Vector3(0.012f, 0.035f, 0.16f), Quaternion.Euler(0f, -6f, 0f), metal);
                DecoPart(PrimitiveType.Cylinder, "Pivot", toolPivot, new Vector3(0f, 0f, 0.03f), new Vector3(0.015f, 0.008f, 0.015f), Quaternion.Euler(90f, 0f, 0f), metal);
                DecoPart(PrimitiveType.Cylinder, "Grip L", toolPivot, new Vector3(-0.03f, 0f, -0.06f), new Vector3(0.03f, 0.012f, 0.03f), Quaternion.Euler(90f, 0f, 0f), accent);
                DecoPart(PrimitiveType.Cylinder, "Grip R", toolPivot, new Vector3(0.03f, 0f, -0.06f), new Vector3(0.03f, 0.012f, 0.03f), Quaternion.Euler(90f, 0f, 0f), accent);
                break;

            case ToolKind.Tape:
                DecoPart(PrimitiveType.Cylinder, "Roll", toolPivot, new Vector3(0f, 0f, 0.02f), new Vector3(0.07f, 0.03f, 0.07f), Quaternion.Euler(90f, 0f, 0f), accent);
                DecoPart(PrimitiveType.Cylinder, "Core", toolPivot, new Vector3(0f, 0f, 0.02f), new Vector3(0.032f, 0.035f, 0.032f), Quaternion.Euler(90f, 0f, 0f), MakeMaterial(new Color(0.85f, 0.83f, 0.78f), 0.05f, 0.5f));
                DecoPart(PrimitiveType.Cube, "Strip", toolPivot, new Vector3(0.03f, -0.05f, 0.09f), new Vector3(0.03f, 0.005f, 0.11f), Quaternion.Euler(24f, 0f, 0f), MakeMaterial(new Color(0.88f, 0.86f, 0.82f), 0.05f, 0.5f));
                break;

            case ToolKind.Tester:
                DecoPart(PrimitiveType.Cube, "Body", toolPivot, new Vector3(0f, 0f, 0.02f), new Vector3(0.03f, 0.03f, 0.16f), Quaternion.identity, accent);
                DecoPart(PrimitiveType.Cylinder, "Tip", toolPivot, new Vector3(0f, 0f, 0.14f), new Vector3(0.008f, 0.03f, 0.008f), Quaternion.Euler(90f, 0f, 0f), metal);
                DecoPart(PrimitiveType.Cube, "Lamp", toolPivot, new Vector3(0f, 0.02f, -0.02f), new Vector3(0.022f, 0.022f, 0.022f), Quaternion.identity, MakeMaterial(new Color(0.4f, 1f, 0.45f), 0f, 0.6f));
                break;
        }
    }

    // 第一人称手持工具（挂在相机下，随视角移动）
    private void BuildViewmodel()
    {
        Material sleeve = MakeMaterial(new Color(0.16f, 0.34f, 0.48f), 0.05f, 0.35f);
        Transform cam = viewCamera.transform;

        // 两条前臂，从画面下方伸向工具
        MakePrimitive(PrimitiveType.Cube, "View Left Arm", cam, new Vector3(0.05f, -0.42f, 0.36f), new Vector3(0.1f, 0.1f, 0.34f), Quaternion.Euler(-10f, 12f, 0f), sleeve);
        MakePrimitive(PrimitiveType.Cube, "View Right Arm", cam, new Vector3(0.3f, -0.44f, 0.34f), new Vector3(0.1f, 0.1f, 0.36f), Quaternion.Euler(-8f, -10f, 0f), sleeve);

        // 手持工具的挂点（具体模型由 BuildToolModel 按当前工具生成）
        toolPivot = new GameObject("Tool").transform;
        toolPivot.SetParent(cam, false);
        toolPivot.localPosition = new Vector3(0.19f, -0.31f, 0.5f);
        toolPivot.localRotation = Quaternion.Euler(-8f, -14f, 4f);
    }

    // ── 音效（运行时合成，不依赖音频素材）─────────────────
    private void BuildAudio()
    {
        footstepSource = player.AddComponent<AudioSource>();
        footstepSource.playOnAwake = false;
        footstepSource.spatialBlend = 0f;
        voiceSource = gameObject.AddComponent<AudioSource>();
        voiceSource.playOnAwake = false;
        voiceSource.spatialBlend = 0f;
        voiceSource.volume = 0.55f;

        workSource = player.AddComponent<AudioSource>();
        workSource.playOnAwake = false;
        workSource.spatialBlend = 0f;
        workClip = CreateWorkClip();

        ambientSource = player.AddComponent<AudioSource>();
        ambientSource.playOnAwake = false;
        ambientSource.spatialBlend = 0f;
        ambientSource.loop = true;
        ambientSource.volume = 0.35f;
        ambientSource.clip = CreateAmbientClip();
        ambientSource.Play();

        musicSource = player.AddComponent<AudioSource>();
        musicSource.playOnAwake = false;
        musicSource.spatialBlend = 0f;
        musicSource.loop = true;
        musicSource.volume = 0.28f;
        musicSource.clip = CreateMusicClip();
        musicSource.Play();

        footstepClip = CreateFootstepClip();
        voiceBlips = new AudioClip[5];
        float[] freqs = { 210f, 245f, 280f, 320f, 175f };
        for (int i = 0; i < voiceBlips.Length; i++)
        {
            voiceBlips[i] = CreateBlipClip(freqs[i], 0.11f);
        }

        uiSource = gameObject.AddComponent<AudioSource>();
        uiSource.playOnAwake = false;
        uiSource.spatialBlend = 0f;
        uiSource.volume = 0.5f;
        uiClickClip = CreateBlipClip(880f, 0.07f);

        notifySource = gameObject.AddComponent<AudioSource>();
        notifySource.playOnAwake = false;
        notifySource.spatialBlend = 0f;
        notifySource.volume = 0.6f;
        notifyClip = CreateChimeClip();
    }

    private void PlayUiClick()
    {
        if (uiSource != null && uiClickClip != null)
        {
            uiSource.PlayOneShot(uiClickClip, 0.5f);
        }
    }

    private void PlayNotify()
    {
        if (notifySource != null && notifyClip != null)
        {
            notifySource.PlayOneShot(notifyClip, 0.6f);
        }
    }

    private AudioClip CreateChimeClip()
    {
        int rate = 44100;
        float duration = 0.45f;
        float[] data = new float[(int)(rate * duration)];
        for (int i = 0; i < data.Length; i++)
        {
            float t = i / (float)rate;
            float freq = t < 0.18f ? 659f : 880f;
            float env = Mathf.Exp(-5f * t);
            data[i] = Mathf.Sin(2f * Mathf.PI * freq * t) * env * 0.45f;
        }
        AudioClip clip = AudioClip.Create("Chime", data.Length, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // 施工特效：粉尘/碎屑粒子，每次点击左键迸发
    private void BuildEffects()
    {
        GameObject fx = new GameObject("施工特效");
        fx.transform.SetParent(transform, false);
        dustEffect = fx.AddComponent<ParticleSystem>();

        ParticleSystem.MainModule main = dustEffect.main;
        main.startLifetime = 0.7f;
        main.startSpeed = 2.4f;
        main.startSize = 0.08f;
        main.startColor = new Color(0.86f, 0.83f, 0.74f, 0.9f);
        main.gravityModifier = 0.9f;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 240;

        ParticleSystem.EmissionModule emission = dustEffect.emission;
        emission.enabled = false;   // 由代码 Emit 触发

        ParticleSystem.ShapeModule shape = dustEffect.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.14f;

        dustEffect.Stop();

        // 粒子材质显式指定，避免打包时被裁掉
        Shader particleShader = Shader.Find("Particles/Standard Unlit");
        if (particleShader == null)
        {
            particleShader = Shader.Find("Sprites/Default");
        }
        if (particleShader != null)
        {
            Material particleMaterial = new Material(particleShader);
            particleMaterial.color = Color.white;
            ParticleSystemRenderer fxRenderer = fx.GetComponent<ParticleSystemRenderer>();
            if (fxRenderer != null)
            {
                fxRenderer.sharedMaterial = particleMaterial;
            }
        }
    }

    private void EmitWorkEffect(Vector3 position)
    {
        if (dustEffect == null)
        {
            return;
        }
        dustEffect.transform.position = position + Vector3.up * 0.9f;
        dustEffect.Emit(16);
        if (workSource != null && workClip != null)
        {
            workSource.pitch = Random.Range(0.9f, 1.15f);
            workSource.PlayOneShot(workClip, 0.55f);
        }
    }

    // 环境音：低沉风声（滤波噪声），12 秒循环
    private static AudioClip CreateAmbientClip()
    {
        const int rate = 22050;
        int length = rate * 12;
        float[] data = new float[length];
        System.Random rng = new System.Random(909);
        float low = 0f;
        for (int i = 0; i < length; i++)
        {
            float white = (float)rng.NextDouble() * 2f - 1f;
            low += (white - low) * 0.008f;                 // 一阶低通 → 风声
            float swell = 0.6f + 0.4f * Mathf.Sin(i / (float)rate * 0.28f);
            data[i] = low * swell * 1.6f;
        }
        AudioClip clip = AudioClip.Create("Ambient", length, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // BGM：C-Am-F-G 四和弦缓慢铺垫，16 秒循环
    private static AudioClip CreateMusicClip()
    {
        const int rate = 22050;
        const float chordSeconds = 4f;
        float[] roots = { 130.81f, 110.00f, 87.31f, 98.00f };   // C3 A2 F2 G2
        int[] thirds = { 4, 3, 4, 4 };                          // 大三/小三度
        int length = (int)(rate * chordSeconds * roots.Length);
        float[] data = new float[length];
        for (int c = 0; c < roots.Length; c++)
        {
            int start = (int)(c * chordSeconds * rate);
            int count = (int)(chordSeconds * rate);
            for (int i = 0; i < count && start + i < length; i++)
            {
                float t = i / (float)rate;
                // 每和弦内缓慢起落，避免爆音
                float env = Mathf.Sin(Mathf.Clamp01(t / chordSeconds) * Mathf.PI);
                float root = roots[c];
                float third = root * Mathf.Pow(2f, thirds[c] / 12f);
                float fifth = root * Mathf.Pow(2f, 7f / 12f);
                float sample =
                    Mathf.Sin(2f * Mathf.PI * root * t) * 0.5f +
                    Mathf.Sin(2f * Mathf.PI * third * t) * 0.32f +
                    Mathf.Sin(2f * Mathf.PI * fifth * t) * 0.28f;
                data[start + i] += sample * env * 0.16f;
            }
        }
        AudioClip clip = AudioClip.Create("Music", length, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private void ToggleMute()
    {
        audioMuted = !audioMuted;
        if (ambientSource != null)
        {
            ambientSource.mute = audioMuted;
        }
        if (musicSource != null)
        {
            musicSource.mute = audioMuted;
        }
        ShowToast(audioMuted ? "已静音（P 恢复）" : "声音已开启", 2.5f);
    }

    // 施工音：短促冲击 + 噪声，模拟敲击/钻削
    private static AudioClip CreateWorkClip()
    {
        const int rate = 44100;
        int length = (int)(rate * 0.18f);
        float[] data = new float[length];
        System.Random rng = new System.Random(4242);
        for (int i = 0; i < length; i++)
        {
            float t = (float)i / rate;
            float envelope = Mathf.Exp(-t * 26f);
            float impact = Mathf.Sin(2f * Mathf.PI * 160f * t) * 0.6f;
            float grind = ((float)rng.NextDouble() * 2f - 1f) * 0.35f;
            data[i] = (impact + grind) * envelope * 0.45f;
        }
        AudioClip clip = AudioClip.Create("Work", length, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private static AudioClip CreateFootstepClip()
    {
        const int rate = 44100;
        int length = (int)(rate * 0.16f);
        float[] data = new float[length];
        System.Random rng = new System.Random(20250917);
        for (int i = 0; i < length; i++)
        {
            float t = (float)i / rate;
            float envelope = Mathf.Exp(-t * 34f);
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.5f;
            float thud = Mathf.Sin(2f * Mathf.PI * 92f * t) * 0.7f;
            data[i] = (noise + thud) * envelope * 0.4f;
        }
        AudioClip clip = AudioClip.Create("Footstep", length, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private static AudioClip CreateBlipClip(float frequency, float duration)
    {
        const int rate = 44100;
        int length = Mathf.Max(1, (int)(rate * duration));
        float[] data = new float[length];
        for (int i = 0; i < length; i++)
        {
            float t = (float)i / rate;
            // 短促起音 + 快速衰减，做成卡通配音的"哔哔"声
            float envelope = Mathf.Min(1f, t / 0.006f) * Mathf.Exp(-t * 24f);
            float square = Mathf.Sign(Mathf.Sin(2f * Mathf.PI * frequency * t)) * 0.5f;
            float overtone = Mathf.Sin(2f * Mathf.PI * frequency * 2.2f * t) * 0.3f;
            data[i] = (square + overtone) * envelope * 0.3f;
        }
        AudioClip clip = AudioClip.Create("Blip", length, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private void SpeakBlip()
    {
        if (voiceSource == null || voiceBlips == null || voiceBlips.Length == 0)
        {
            return;
        }
        voiceTimer = Random.Range(0.07f, 0.12f);
        voiceSource.pitch = Random.Range(0.85f, 1.18f);
        voiceSource.PlayOneShot(voiceBlips[Random.Range(0, voiceBlips.Length)], 0.5f);
    }

    // ── 开场 NPC 与对话 ───────────────────────────────────
    private void BuildNpc()
    {
        // 工头站在工位旁，面朝玩家
        bossRig = BuildCharacterModel("Boss", new Vector3(-14.8f, 0f, -2.2f), 0f,
            new Color(0.62f, 0.3f, 0.22f), new Color(0.83f, 0.66f, 0.5f));
    }

    // 角色骨架：四肢挂在枢轴下，才能摆臂迈腿（否则移动时像"飘"）
    private class CharacterRig
    {
        public Transform root;
        public Transform body;
        public Transform leftArm, rightArm, leftLeg, rightLeg;
        public Transform leftKnee, rightKnee;   // 膝关节，用于坐姿
    }

    private CharacterRig BuildCharacterModel(string name, Vector3 position, float yaw, Color cloth, Color skin)
    {
        GameObject root = new GameObject(name);
        root.transform.SetParent(transform, false);
        root.transform.position = position;
        root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

        Material clothMaterial = MakeMaterial(cloth, 0.05f, 0.35f);
        Material skinMaterial = MakeMaterial(skin, 0.02f, 0.3f);
        Material trouserMaterial = MakeMaterial(new Color(0.22f, 0.26f, 0.3f), 0.05f, 0.3f);
        Material helmetMaterial = MakeMaterial(new Color(0.95f, 0.72f, 0.12f), 0.1f, 0.45f);

        // 躯干分胸/腰两段，比例更像人
        Transform body = MakePrimitive(PrimitiveType.Cube, "Chest", root.transform, new Vector3(0f, 1.02f, 0f), new Vector3(0.48f, 0.44f, 0.3f), Quaternion.identity, clothMaterial).transform;
        MakePrimitive(PrimitiveType.Cube, "Waist", root.transform, new Vector3(0f, 0.68f, 0f), new Vector3(0.4f, 0.3f, 0.26f), Quaternion.identity, clothMaterial);
        MakePrimitive(PrimitiveType.Cube, "Neck", root.transform, new Vector3(0f, 1.26f, 0f), new Vector3(0.13f, 0.12f, 0.13f), Quaternion.identity, skinMaterial);
        MakePrimitive(PrimitiveType.Cube, "Shoulder L", root.transform, new Vector3(-0.28f, 1.17f, 0f), new Vector3(0.14f, 0.12f, 0.18f), Quaternion.identity, clothMaterial);
        MakePrimitive(PrimitiveType.Cube, "Shoulder R", root.transform, new Vector3(0.28f, 1.17f, 0f), new Vector3(0.14f, 0.12f, 0.18f), Quaternion.identity, clothMaterial);
        // 头/安全帽挂在根节点上（绝对高度），不受身体动画影响，保证始终可见
        MakePrimitive(PrimitiveType.Cube, "Head", root.transform, new Vector3(0f, 1.44f, 0f), new Vector3(0.32f, 0.32f, 0.32f), Quaternion.identity, skinMaterial);
        MakePrimitive(PrimitiveType.Cube, "Helmet", root.transform, new Vector3(0f, 1.63f, 0f), new Vector3(0.4f, 0.1f, 0.4f), Quaternion.identity, helmetMaterial);

        // 五官（脸在角色朝向的 +Z 面）
        Material faceMaterial = MakeMaterial(new Color(0.12f, 0.11f, 0.12f), 0.02f, 0.5f);
        Material mouthMaterial = MakeMaterial(new Color(0.62f, 0.28f, 0.26f), 0.02f, 0.45f);
        Material noseMaterial = MakeMaterial(skin * 0.88f, 0.02f, 0.3f);
        MakePrimitive(PrimitiveType.Cube, "Eye L", root.transform, new Vector3(-0.075f, 1.475f, 0.165f), new Vector3(0.055f, 0.055f, 0.02f), Quaternion.identity, faceMaterial);
        MakePrimitive(PrimitiveType.Cube, "Eye R", root.transform, new Vector3(0.075f, 1.475f, 0.165f), new Vector3(0.055f, 0.055f, 0.02f), Quaternion.identity, faceMaterial);
        MakePrimitive(PrimitiveType.Cube, "Brow L", root.transform, new Vector3(-0.075f, 1.522f, 0.163f), new Vector3(0.075f, 0.018f, 0.02f), Quaternion.identity, faceMaterial);
        MakePrimitive(PrimitiveType.Cube, "Brow R", root.transform, new Vector3(0.075f, 1.522f, 0.163f), new Vector3(0.075f, 0.018f, 0.02f), Quaternion.identity, faceMaterial);
        MakePrimitive(PrimitiveType.Cube, "Nose", root.transform, new Vector3(0f, 1.425f, 0.175f), new Vector3(0.045f, 0.06f, 0.035f), Quaternion.identity, noseMaterial);
        MakePrimitive(PrimitiveType.Cube, "Mouth", root.transform, new Vector3(0f, 1.375f, 0.165f), new Vector3(0.095f, 0.025f, 0.02f), Quaternion.identity, mouthMaterial);

        Transform leftArm = new GameObject("Left Arm Pivot").transform;
        leftArm.SetParent(root.transform, false);
        leftArm.localPosition = new Vector3(-0.34f, 1.12f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Arm", leftArm, new Vector3(0f, -0.25f, 0f), new Vector3(0.12f, 0.5f, 0.12f), Quaternion.identity, clothMaterial);
        MakePrimitive(PrimitiveType.Cube, "Hand", leftArm, new Vector3(0f, -0.53f, 0f), new Vector3(0.11f, 0.1f, 0.11f), Quaternion.identity, skinMaterial);

        Transform rightArm = new GameObject("Right Arm Pivot").transform;
        rightArm.SetParent(root.transform, false);
        rightArm.localPosition = new Vector3(0.34f, 1.12f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Arm", rightArm, new Vector3(0f, -0.25f, 0f), new Vector3(0.12f, 0.5f, 0.12f), Quaternion.identity, clothMaterial);
        MakePrimitive(PrimitiveType.Cube, "Hand", rightArm, new Vector3(0f, -0.53f, 0f), new Vector3(0.11f, 0.1f, 0.11f), Quaternion.identity, skinMaterial);

        // 每条腿分两段：髋枢轴（大腿）→ 膝枢轴（小腿），这样才能坐下
        Transform leftLeg = new GameObject("Left Leg Pivot").transform;
        leftLeg.SetParent(root.transform, false);
        leftLeg.localPosition = new Vector3(-0.13f, 0.62f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Thigh", leftLeg, new Vector3(0f, -0.15f, 0f), new Vector3(0.16f, 0.3f, 0.16f), Quaternion.identity, trouserMaterial);
        Transform leftKnee = new GameObject("Left Knee").transform;
        leftKnee.SetParent(leftLeg, false);
        leftKnee.localPosition = new Vector3(0f, -0.3f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Shin", leftKnee, new Vector3(0f, -0.15f, 0f), new Vector3(0.16f, 0.3f, 0.16f), Quaternion.identity, trouserMaterial);
        MakePrimitive(PrimitiveType.Cube, "Foot", leftKnee, new Vector3(0f, -0.32f, 0.05f), new Vector3(0.16f, 0.08f, 0.26f), Quaternion.identity, trouserMaterial);

        Transform rightLeg = new GameObject("Right Leg Pivot").transform;
        rightLeg.SetParent(root.transform, false);
        rightLeg.localPosition = new Vector3(0.13f, 0.62f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Thigh", rightLeg, new Vector3(0f, -0.15f, 0f), new Vector3(0.16f, 0.3f, 0.16f), Quaternion.identity, trouserMaterial);
        Transform rightKnee = new GameObject("Right Knee").transform;
        rightKnee.SetParent(rightLeg, false);
        rightKnee.localPosition = new Vector3(0f, -0.3f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Shin", rightKnee, new Vector3(0f, -0.15f, 0f), new Vector3(0.16f, 0.3f, 0.16f), Quaternion.identity, trouserMaterial);
        MakePrimitive(PrimitiveType.Cube, "Foot", rightKnee, new Vector3(0f, -0.32f, 0.05f), new Vector3(0.16f, 0.08f, 0.26f), Quaternion.identity, trouserMaterial);

        Collider[] colliders = root.GetComponentsInChildren<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            colliders[i].enabled = false;
        }

        return new CharacterRig
        {
            root = root.transform,
            body = body,
            leftArm = leftArm,
            rightArm = rightArm,
            leftLeg = leftLeg,
            rightLeg = rightLeg,
            leftKnee = leftKnee,
            rightKnee = rightKnee
        };
    }

    // 行走摆臂迈腿；moving 为 false 时回到站立姿态
    private void AnimateRig(CharacterRig rig, bool moving, float swingScale)
    {
        if (rig == null || rig.body == null)
        {
            return;
        }
        float t = Time.time * 8.5f;
        if (moving)
        {
            float swing = Mathf.Sin(t) * 28f * swingScale;
            rig.leftArm.localRotation = Quaternion.Euler(swing, 0f, 0f);
            rig.rightArm.localRotation = Quaternion.Euler(-swing, 0f, 0f);
            rig.leftLeg.localRotation = Quaternion.Euler(-swing, 0f, 0f);
            rig.rightLeg.localRotation = Quaternion.Euler(swing, 0f, 0f);
            rig.body.localPosition = new Vector3(0f, 1.02f + Mathf.Abs(Mathf.Sin(t)) * 0.04f, 0f);
        }
        else
        {
            rig.leftArm.localRotation = Quaternion.identity;
            rig.rightArm.localRotation = Quaternion.identity;
            rig.leftLeg.localRotation = Quaternion.identity;
            rig.rightLeg.localRotation = Quaternion.identity;
            rig.body.localPosition = new Vector3(0f, 1.02f, 0f);
        }
    }

    private void BuildIntroDialogue()
    {
        dialogue.Add(new DialogueLine { speaker = "工头 老张", text = "小陈，来活儿了。城东那片老小区，问题一堆，业主催得紧。" });
        dialogue.Add(new DialogueLine { speaker = "工头 老张", text = "各家的户主会自己到前台来登记，或者打电话预约，单子就记下了。" });
        dialogue.Add(new DialogueLine { speaker = "工头 老张", text = "带上工具去现场，走到问题跟前，连点几下左键就能开工，干完活记得收费。" });
        dialogue.Add(new DialogueLine { speaker = "工头 老张", text = "先给你配了把活动扳手，管件那几单够你练手了。" });
        dialogue.Add(new DialogueLine { speaker = "工头 老张", text = "挣了钱去商店添几件趁手的家伙，能接的活才多。去吧！" });
    }

    // 是否看过开场工头嘱托（按账号记录，只第一次强制播放）
    private string IntroSeenKey
    {
        get { return "kitchen_intro_seen"; }   // 全局只播一次，不按账号区分
    }
    private bool HasSeenIntro()
    {
        return PlayerPrefs.GetInt(IntroSeenKey, 0) == 1;
    }
    private void SetIntroSeen()
    {
        PlayerPrefs.SetInt(IntroSeenKey, 1);
        PlayerPrefs.Save();
    }

    // 把「看过开场嘱托」标记到云端账号，跨设备/清缓存也有效
    private void MarkIntroSeenCloud()
    {
        if (string.IsNullOrEmpty(currentAccount) || currentAccount == "游客" || !CloudEnabled)
        {
            return;
        }
        string body = "{\"intro_seen\":true}";
        StartCoroutine(SupabaseRequest("PATCH", "/rest/v1/accounts?username=eq." + UnityWebRequest.EscapeURL(currentAccount), body, null));
    }

    // 主动找工头听嘱托
    private void StartBossBriefing()
    {
        talkTarget = null;
        dialogue.Clear();
        BuildIntroDialogue();
        dialogueIndex = 0;
        BeginLine();
    }

    private void BeginLine()
    {
        typeTimer = 0f;
        voiceBurst = 1.4f;
        SpeakBlip();
    }

    private bool IsLineFullyShown()
    {
        if (dialogueIndex < 0 || dialogueIndex >= dialogue.Count)
        {
            return true;
        }
        return typeTimer * TypeCharsPerSecond >= dialogue[dialogueIndex].text.Length;
    }

    private int VisibleCharCount()
    {
        if (dialogueIndex < 0 || dialogueIndex >= dialogue.Count)
        {
            return 0;
        }
        return Mathf.Clamp(Mathf.FloorToInt(typeTimer * TypeCharsPerSecond), 0, dialogue[dialogueIndex].text.Length);
    }

    private void EndConversation()
    {
        dialogueIndex = -1;
        dialogue.Clear();
        dialogueJustEnded = true;   // 本帧不再接受新的对话触发

        if (talkTarget != null)
        {
            // 户主说完 → 登记工单 → 回家
            FileReport(talkTarget);
            talkTarget.phase = 2;
            talkTarget.moving = true;
            talkTarget = null;
        }
        else if (!introDone)
        {
            introDone = true;
            orderTimer = 1.5f;
            ShowToast("各家户主会陆续上门反映问题，听他们说完就能接单", 7f);
        }
    }

    // 户主说话风格：每次随机组合，避免所有人都一个腔调
    private static readonly string[] Greetings =
    {
        "师傅，打扰一下，方便说两句吗？",
        "哎，师傅！可算找着人了。",
        "师傅，忙着呢？耽误您两分钟。",
        "请问是陈师傅吗？我找您有点事。",
        "师傅！总算等到您了。",
        "您好您好，我是咱这片的住户。",
        "师傅，您这活儿接不接？",
        "不好意思打扰了，家里有点麻烦事。",
    };

    private static readonly string[] Complaints =
    {
        "我家{ROOM}有点毛病 —— {TITLE}。",
        "跟您说个事儿，我家{ROOM}的{TITLE}，看着不太对劲。",
        "我家{ROOM}出问题了，好像是{TITLE}。",
        "麻烦您给看看，我家{ROOM}{TITLE}，我瞅着挺严重的。",
        "师傅，我家{ROOM}那个{TITLE}，您有空给瞧瞧呗？",
        "我家{ROOM}的{TITLE}，一直拖着没弄，您看还能修不？",
        "是这样，我家{ROOM}的{TITLE}，我自己弄不明白。",
        "师傅您给掌掌眼，我家{ROOM}是不是{TITLE}了？",
    };

    private static readonly string[] Details =
    {
        "{CAUSE}。您受累给看看，多少钱我出。",
        "我瞧着是{CAUSE}。该修就修，价钱好说。",
        "具体我也不太懂，反正就是{CAUSE}。您拿个主意。",
        "{CAUSE}。这事儿拖挺久了，您给想想办法。",
        "应该是{CAUSE}吧？您比我懂，听您的。",
        "{CAUSE}。您方便的时候过去看看就成。",
    };

    private static readonly string[] Replies =
    {
        "行，我记下了，这就带上工具过去。",
        "好嘞，我收拾下工具马上到。",
        "明白，包在我身上。",
        "没问题，我这就过去瞧瞧。",
        "成，您放心，交给我了。",
    };

    private static string Pick(string[] options)
    {
        return options[Random.Range(0, options.Length)];
    }

    // 户主上门对话（原神式多句对话，台词随机组合）
    private void StartConversation(Homeowner owner)
    {
        talkTarget = owner;
        dialogue.Clear();

        string who = owner.room.name + " 户主";
        string complaint = Pick(Complaints)
            .Replace("{ROOM}", owner.room.type)
            .Replace("{TITLE}", owner.template.title);
        string detail = Pick(Details).Replace("{CAUSE}", owner.template.cause);

        dialogue.Add(new DialogueLine { speaker = who, text = Pick(Greetings) });
        dialogue.Add(new DialogueLine { speaker = who, text = complaint });
        dialogue.Add(new DialogueLine { speaker = who, text = detail });
        dialogue.Add(new DialogueLine { speaker = "陈师傅", text = Pick(Replies) });

        dialogueIndex = 0;
        BeginLine();
    }

    private void HandleDialogue()
    {
        // 开场：等玩家站定后再由工头开口
        if (!introStarted)
        {
            introDelay -= Time.deltaTime;
            if (introDelay <= 0f)
            {
                introStarted = true;
                if (introSeenCloud || HasSeenIntro())
                {
                    // 已看过开场嘱托，跳过；之后可主动找工头听
                    introDone = true;
                    return;
                }
                SetIntroSeen();   // 本地也标记一份
                introSeenCloud = true;
                MarkIntroSeenCloud();
                BuildIntroDialogue();
                dialogueIndex = 0;   // 必须置 0，否则对话永远不开始、introDone 也永远不为 true（派单会整个停摆）
                BeginLine();
            }
            return;
        }

        if (dialogueIndex < 0)
        {
            return;
        }

        // 逐字显示
        typeTimer += Time.deltaTime;
        if (typeTimer > DialogueTypeTime)
        {
            typeTimer = DialogueTypeTime;
        }

        // 每句只"嘟"一小段就安静下来，按键进入下一句才重新发声
        if (voiceBurst > 0f)
        {
            voiceBurst -= Time.deltaTime;
            voiceTimer -= Time.deltaTime;
            if (voiceTimer <= 0f)
            {
                SpeakBlip();
            }
        }

        if (Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Space))
        {
            // 还没显示完先补全，已显示完则进入下一句
            if (!IsLineFullyShown())
            {
                typeTimer = DialogueTypeTime;
                return;
            }

            dialogueIndex++;
            if (dialogueIndex >= dialogue.Count)
            {
                EndConversation();
                return;
            }
            BeginLine();
        }
    }

    // ── 移动（手动 AABB 碰撞）────────────────────────────
    private void HandleMovement()
    {
        if (player == null)
        {
            return;
        }

        // 对话中或维修中不允许移动 / 跳跃
        bool blocked = repairingOrder != null || dialogueIndex >= 0 || sleeping;

        // 垂直：真实重力 + 跳跃；落地即停，绝不穿地
        if (grounded && !blocked && Input.GetKeyDown(KeyCode.Space))
        {
            verticalVelocity = JumpSpeed;
            grounded = false;
        }
        verticalVelocity -= Gravity * Time.deltaTime;
        playerPosition.y += verticalVelocity * Time.deltaTime;
        if (playerPosition.y <= GroundLevel)
        {
            playerPosition.y = GroundLevel;
            verticalVelocity = 0f;
            grounded = true;
        }

        Vector3 input;
        if (shopOpen)
        {
            // 商店占用方向键，此时移动只认 WASD，避免同一按键既切换商品又移动角色
            float h = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
            float v = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
            input = new Vector3(h, 0f, v);
        }
        else
        {
            input = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
        }
        input = Vector3.ClampMagnitude(input, 1f);
        bool moving = input.sqrMagnitude > 0.01f && !blocked;

        if (moving)
        {
            // 以视角朝向为基准移动；按住 Shift 加速跑
            bool running = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float speed = MoveSpeed * (running ? 1.8f : 1f);
            running_ = running;

            Vector3 direction = Quaternion.Euler(0f, lookYaw, 0f) * input.normalized;
            Vector3 move = direction * speed * Time.deltaTime;
            Vector3 target = playerPosition + move;

            if (!Collides(target))
            {
                playerPosition = target;
            }
            else
            {
                // 分离轴滑动，让角色贴墙走
                Vector3 xOnly = new Vector3(playerPosition.x + move.x, 0f, playerPosition.z);
                if (!Collides(xOnly))
                {
                    playerPosition = xOnly;
                }
                Vector3 zOnly = new Vector3(playerPosition.x, 0f, playerPosition.z + move.z);
                if (!Collides(zOnly))
                {
                    playerPosition = zOnly;
                }
            }
        }

        walking = moving;
        if (!moving)
        {
            running_ = false;
        }
        player.transform.position = playerPosition;
        UpdateFootsteps();
    }

    // 走路脚步声：与步伐同步，音量随机微变避免机械感
    private void UpdateFootsteps()
    {
        if (footstepSource == null || footstepClip == null)
        {
            return;
        }

        if (walking && grounded)
        {
            stepTimer -= Time.deltaTime;
            if (stepTimer <= 0f)
            {
                stepTimer = running_ ? StepInterval * 0.62f : StepInterval;
                footstepSource.pitch = Random.Range(0.9f, 1.1f) * (running_ ? 1.12f : 1f);
                footstepSource.PlayOneShot(footstepClip, running_ ? 0.62f : 0.5f);
            }
        }
        else
        {
            stepTimer = 0f;
        }
    }

    private bool Collides(Vector3 position)
    {
        return Collides(position, false);
    }

    // ignoreDoorsAndNpcs：NPC 自己移动时用 true（可以穿过关着的房门，也不会被别的 NPC 挡住）
    private bool Collides(Vector3 position, bool ignoreDoorsAndNpcs)
    {
        Vector2 p = new Vector2(position.x, position.z);
        float bodyBottom = position.y;
        float bodyTop = position.y + 1.7f;

        for (int i = 0; i < obstacles.Count; i++)
        {
            Bounds b = obstacles[i];
            // 垂直方向不重叠就不算碰撞：门楣、窗楣在头顶上方，不该挡路
            if (b.max.y <= bodyBottom || b.min.y >= bodyTop)
            {
                continue;
            }
            float closestX = Mathf.Clamp(p.x, b.min.x, b.max.x);
            float closestZ = Mathf.Clamp(p.y, b.min.z, b.max.z);
            float dx = p.x - closestX;
            float dz = p.y - closestZ;
            if (dx * dx + dz * dz < PlayerRadius * PlayerRadius)
            {
                return true;
            }
        }

        if (ignoreDoorsAndNpcs)
        {
            return false;
        }

        // 关着的房门也阻挡通行
        for (int i = 0; i < houseDoors.Count; i++)
        {
            HouseDoor door = houseDoors[i];
            if (door.open > 0.5f)
            {
                continue;
            }
            float nearestX = Mathf.Clamp(p.x, door.xMin, door.xMax);
            float ox = p.x - nearestX;
            float oz = p.y - door.z;
            if (ox * ox + oz * oz < PlayerRadius * PlayerRadius)
            {
                return true;
            }
        }

        // NPC 身体也挡人，玩家不能从户主身上穿过去
        const float npcRadius = 0.46f;
        for (int i = 0; i < homeowners.Count; i++)
        {
            if (homeowners[i].rig == null || homeowners[i].rig.root == null)
            {
                continue;
            }
            Vector3 npc = homeowners[i].rig.root.position;
            float dx = position.x - npc.x;
            float dz = position.z - npc.z;
            float rr = PlayerRadius + npcRadius;
            if (dx * dx + dz * dz < rr * rr)
            {
                return true;
            }
        }

        return false;
    }

    // 行走摆臂 + 手持工具动作
    private void UpdateAnimate()
    {
        if (playerBody == null)
        {
            return;
        }

        // 左键点击触发挥动动作（无论是否维修，第一/第三人称都生效）
        if (Input.GetMouseButtonDown(0) && cursorLocked && loggedIn && !IsPointerOverGui(Input.mousePosition))
        {
            toolStrike = 1f;
        }

        float t = Time.time * (running_ ? 12.5f : 9f);
        if (repairingOrder != null || toolStrike > 0f)
        {
            // 施工/点击：双臂前伸摆动
            float s = toolStrike * toolStrike;
            float idle = repairingOrder != null ? 30f : 0f;
            float push = s * 42f;
            float shake = toolStrike * Mathf.Sin(Time.time * 58f) * 6f;
            leftArmPivot.localRotation = Quaternion.Euler(-idle - push + shake, 0f, 0f);
            rightArmPivot.localRotation = Quaternion.Euler(-idle - push - shake, 0f, 0f);
            leftLegPivot.localRotation = Quaternion.identity;
            rightLegPivot.localRotation = Quaternion.identity;
            playerBody.localPosition = new Vector3(0f, 0.85f, 0f);
        }
        else if (!grounded)
        {
            // 腾空：收腿抬臂
            leftArmPivot.localRotation = Quaternion.Euler(-38f, 0f, 0f);
            rightArmPivot.localRotation = Quaternion.Euler(-38f, 0f, 0f);
            leftLegPivot.localRotation = Quaternion.Euler(32f, 0f, 0f);
            rightLegPivot.localRotation = Quaternion.Euler(24f, 0f, 0f);
            playerBody.localPosition = new Vector3(0f, 0.85f, 0f);
        }
        else if (walking)
        {
            float swing = Mathf.Sin(t) * (running_ ? 38f : 26f);
            leftArmPivot.localRotation = Quaternion.Euler(swing, 0f, 0f);
            rightArmPivot.localRotation = Quaternion.Euler(-swing, 0f, 0f);
            leftLegPivot.localRotation = Quaternion.Euler(-swing, 0f, 0f);
            rightLegPivot.localRotation = Quaternion.Euler(swing, 0f, 0f);
            playerBody.localPosition = new Vector3(0f, 0.85f + Mathf.Abs(Mathf.Sin(t)) * 0.045f, 0f);
        }
        else
        {
            leftArmPivot.localRotation = Quaternion.identity;
            rightArmPivot.localRotation = Quaternion.identity;
            leftLegPivot.localRotation = Quaternion.identity;
            rightLegPivot.localRotation = Quaternion.identity;
            playerBody.localPosition = new Vector3(0f, 0.85f, 0f);
        }

        UpdateTool();

        // 第三人称下隐藏第一人称手持模型
        if (toolPivot != null)
        {
            toolPivot.gameObject.SetActive(!thirdPerson);
        }
    }

    // 施工时工具来回作业，平时随步伐轻微晃动
    private void UpdateTool()
    {
        if (toolPivot == null)
        {
            return;
        }

        Vector3 basePosition = new Vector3(0.19f, -0.31f, 0.5f);
        Vector3 baseEuler = new Vector3(-8f, -14f, 4f);

        if (repairingOrder != null)
        {
            // 每点一次左键：工具向前猛冲一下并抖动，随后回位，形成"施工"的手感
            float s = toolStrike * toolStrike;
            float push = s * 0.15f;
            float shake = toolStrike * Mathf.Sin(Time.time * 58f) * 5.5f;
            toolPivot.localPosition = basePosition + new Vector3(0f, push * 0.3f, push);
            toolPivot.localRotation = Quaternion.Euler(baseEuler.x + push * 115f, baseEuler.y, baseEuler.z + shake);
        }
        else
        {
            toolAnim = Mathf.Lerp(toolAnim, 0f, Time.deltaTime * 5f);
            float bob = walking ? Mathf.Sin(Time.time * 9f) * 0.022f : Mathf.Sin(Time.time * 1.6f) * 0.006f;
            toolPivot.localPosition = basePosition + new Vector3(0f, bob, 0f);
            toolPivot.localRotation = Quaternion.Euler(baseEuler.x, baseEuler.y, baseEuler.z);
        }
    }

    // ── 工单模板与派单 ────────────────────────────────────
    // ── 工程数据：执行规范 + 材料清单（含真实市场参考价）+ 人工工时 ──
    // 改造成本 = (材料费 × 1.15 材料损耗) + 人工工时 × 人工单价，再计 15% 管理费
    private const int LaborRate = 65;        // 人工单价（元/工时，参考 2025 年一二线城市装修人工）

    private class MaterialItem
    {
        public string name;
        public float qty;
        public string unit;
        public int unitPrice;   // 元

        public MaterialItem(string name, float qty, string unit, int unitPrice)
        {
            this.name = name;
            this.qty = qty;
            this.unit = unit;
            this.unitPrice = unitPrice;
        }

        public int Cost { get { return Mathf.RoundToInt(qty * unitPrice); } }
    }

    private class SpecSheet
    {
        public string standard;
        public float laborHours;
        public MaterialItem[] items;
    }

    private static readonly Dictionary<string, SpecSheet> Specs = new Dictionary<string, SpecSheet>
    {
        { "水槽下方渗漏", new SpecSheet { standard = "GB 50015《建筑给水排水设计标准》", laborHours = 4f, items = new[] {
            new MaterialItem("角阀", 2, "只", 48), new MaterialItem("存水弯", 1, "套", 68),
            new MaterialItem("防水托盘", 1, "个", 90), new MaterialItem("防潮垫层", 1.5f, "㎡", 28) } } },

        { "灶台燃气管老化", new SpecSheet { standard = "GB 50028《城镇燃气设计规范》", laborHours = 3f, items = new[] {
            new MaterialItem("不锈钢波纹管", 1, "根", 120), new MaterialItem("燃气专用接头", 2, "个", 35),
            new MaterialItem("密封垫", 4, "片", 8), new MaterialItem("气密性检测", 1, "次", 150) } } },

        { "橱柜门板变形", new SpecSheet { standard = "GB/T 3324《木家具通用技术条件》", laborHours = 3f, items = new[] {
            new MaterialItem("防潮柜门", 1, "扇", 380), new MaterialItem("液压铰链", 2, "个", 45),
            new MaterialItem("封边条", 4, "m", 12) } } },

        { "冰箱插座接触不良", new SpecSheet { standard = "GB 50096《住宅设计规范》", laborHours = 2f, items = new[] {
            new MaterialItem("16A 插座面板", 1, "个", 65), new MaterialItem("暗盒", 1, "个", 15),
            new MaterialItem("4mm² 铜芯线", 3, "m", 18) } } },

        { "地面瓷砖空鼓", new SpecSheet { standard = "GB 50209《建筑地面工程施工质量验收规范》", laborHours = 5f, items = new[] {
            new MaterialItem("同色地砖", 6, "片", 85), new MaterialItem("瓷砖胶", 1, "袋", 55),
            new MaterialItem("美缝剂", 1, "支", 45) } } },

        { "沙发背景墙开裂", new SpecSheet { standard = "GB 50210《建筑装饰装修工程质量验收标准》", laborHours = 6f, items = new[] {
            new MaterialItem("耐水腻子", 15, "kg", 3), new MaterialItem("玻纤网格布", 2, "㎡", 12),
            new MaterialItem("底漆", 5, "kg", 28), new MaterialItem("面漆", 5, "kg", 32) } } },

        { "电视线缆外露", new SpecSheet { standard = "GB 50303《建筑电气工程施工质量验收规范》", laborHours = 1.5f, items = new[] {
            new MaterialItem("PVC 线槽", 3, "m", 18), new MaterialItem("扎带", 1, "包", 12),
            new MaterialItem("线缆标识", 1, "套", 25) } } },

        { "吊顶灯带脱落", new SpecSheet { standard = "GB 50303《建筑电气工程施工质量验收规范》", laborHours = 2f, items = new[] {
            new MaterialItem("灯带卡扣", 8, "个", 6), new MaterialItem("LED 灯带", 2, "m", 28),
            new MaterialItem("接线端子", 4, "个", 5) } } },

        { "木门变形关不严", new SpecSheet { standard = "GB/T 3324《木家具通用技术条件》", laborHours = 3f, items = new[] {
            new MaterialItem("门铰链", 3, "个", 35), new MaterialItem("防潮封边条", 5, "m", 12),
            new MaterialItem("木器漆", 1, "kg", 85) } } },

        { "墙面返潮发霉", new SpecSheet { standard = "GB 50210《建筑装饰装修工程质量验收标准》", laborHours = 8f, items = new[] {
            new MaterialItem("外墙防水涂料", 20, "kg", 18), new MaterialItem("耐水腻子", 20, "kg", 3),
            new MaterialItem("防霉底漆", 5, "kg", 32) } } },

        { "衣柜滑轨卡顿", new SpecSheet { standard = "GB/T 3324《木家具通用技术条件》", laborHours = 1.5f, items = new[] {
            new MaterialItem("三节滑轨", 2, "套", 85), new MaterialItem("自攻螺丝", 24, "个", 1),
            new MaterialItem("润滑脂", 1, "支", 25) } } },

        { "床头插座松动", new SpecSheet { standard = "GB 50096《住宅设计规范》", laborHours = 1f, items = new[] {
            new MaterialItem("五孔插座", 1, "个", 48), new MaterialItem("暗盒加固件", 1, "套", 20),
            new MaterialItem("绝缘胶带", 1, "卷", 8) } } },

        { "地漏返味", new SpecSheet { standard = "GB 50015《建筑给水排水设计标准》", laborHours = 2f, items = new[] {
            new MaterialItem("防臭地漏芯", 1, "个", 45), new MaterialItem("密封胶", 1, "支", 28),
            new MaterialItem("存水弯", 1, "套", 68) } } },

        { "墙面瓷砖空鼓", new SpecSheet { standard = "GB 50210《建筑装饰装修工程质量验收标准》", laborHours = 5f, items = new[] {
            new MaterialItem("同色墙砖", 8, "片", 45), new MaterialItem("瓷砖胶", 1, "袋", 55),
            new MaterialItem("防水涂料", 5, "kg", 22) } } },

        { "马桶底座渗水", new SpecSheet { standard = "GB 50015《建筑给水排水设计标准》", laborHours = 2f, items = new[] {
            new MaterialItem("法兰密封圈", 1, "个", 55), new MaterialItem("防霉硅酮胶", 1, "支", 35),
            new MaterialItem("膨胀螺栓", 2, "个", 8) } } },
    };

    private static SpecSheet SpecOf(string title)
    {
        SpecSheet spec;
        return Specs.TryGetValue(title, out spec) ? spec : null;
    }

    private static string StandardOf(Order order)
    {
        SpecSheet spec = SpecOf(order.title);
        return spec == null ? "—" : spec.standard;
    }

    // 按真实材料单价核算造价：材料费×1.15(损耗) + 人工费，再计 15% 管理费
    private static int QuoteCost(string title)
    {
        SpecSheet spec = SpecOf(title);
        if (spec == null)
        {
            return 1000;
        }
        float material = 0f;
        for (int i = 0; i < spec.items.Length; i++)
        {
            material += spec.items[i].Cost;
        }
        float labor = spec.laborHours * LaborRate;
        return Mathf.RoundToInt((material * 1.15f + labor) * 1.15f / 10f) * 10;
    }

    // 造价构成：材料费(含 15% 损耗) / 人工费 / 管理费
    private static void CostBreakdown(string title, out int materialCost, out int laborCost, out int manageCost)
    {
        SpecSheet spec = SpecOf(title);
        if (spec == null)
        {
            materialCost = 0;
            laborCost = 0;
            manageCost = 0;
            return;
        }
        float material = 0f;
        for (int i = 0; i < spec.items.Length; i++)
        {
            material += spec.items[i].Cost;
        }
        materialCost = Mathf.RoundToInt(material * 1.15f);
        laborCost = Mathf.RoundToInt(spec.laborHours * LaborRate);
        manageCost = Mathf.RoundToInt((materialCost + laborCost) * 0.15f);
    }

    // 材料清单（带单价），用于面板与报告展示
    private static string PricedMaterials(string title)
    {
        SpecSheet spec = SpecOf(title);
        if (spec == null)
        {
            return "—";
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < spec.items.Length; i++)
        {
            MaterialItem item = spec.items[i];
            if (i > 0)
            {
                sb.Append("　");
            }
            sb.Append(item.name).Append(" ").Append(item.qty.ToString("0.#")).Append(item.unit)
              .Append(" ¥").Append(item.Cost);
        }
        return sb.ToString();
    }

    private static string MaterialsOf(Order order)
    {
        SpecSheet spec = SpecOf(order.title);
        if (spec == null)
        {
            return "—";
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int i = 0; i < spec.items.Length; i++)
        {
            if (i > 0)
            {
                sb.Append("、");
            }
            sb.Append(spec.items[i].name).Append("×").Append(spec.items[i].qty.ToString("0.#")).Append(spec.items[i].unit);
        }
        return sb.ToString();
    }

    private void InitializeOrders()
    {
        // 每个点位挂一个工程传感器（数字孪生的数据源），改造完成后读数回落到正常值
        // 参数：房型, 标题, 成因, 方案, 最低价, 最高价, x, z, 工具, 传感器, 单位, 正常值, 报警阈值, 量程
        // 厨房
        templates.Add(new OrderTemplate("厨房", "水槽下方渗漏", "水槽柜内给水角阀老化，柜底板见渗水痕迹", "更换角阀与存水弯，柜底增设防水托盘", 3200, 4200, -1.5f, 1.7f, ToolKind.Wrench,
            "柜内湿度", "%RH", 45f, 80f, 100f));
        templates.Add(new OrderTemplate("厨房", "灶台燃气管老化", "燃气软管超期服役，接口处有轻微泄漏", "更换不锈钢波纹管并做气密性检测", 2800, 3800, 1.4f, 1.7f, ToolKind.Wrench,
            "可燃气体浓度", "%LEL", 2f, 10f, 25f, false));
        templates.Add(new OrderTemplate("厨房", "橱柜门板变形", "地柜门板受潮变形，开合卡顿异响", "更换门板并调整铰链，柜体做防潮处理", 1200, 2000, 0f, 1.1f, ToolKind.Drill,
            "门板形变量", "mm", 0.5f, 3f, 8f));
        templates.Add(new OrderTemplate("厨房", "冰箱插座接触不良", "冰箱专用插座松动，插头发热变色", "更换 16A 插座面板并紧固线路", 900, 1600, 1.9f, -1.6f, ToolKind.Tester,
            "插座温升", "℃", 25f, 55f, 90f));

        // 客厅
        templates.Add(new OrderTemplate("客厅", "地面瓷砖空鼓", "地面瓷砖局部空鼓脱层，踩踏有松动异响", "空鼓砖拆除重铺，基层找平做界面处理", 1800, 2800, 0f, -0.6f, ToolKind.Hammer,
            "地面空鼓率", "%", 2f, 15f, 40f));
        templates.Add(new OrderTemplate("客厅", "沙发背景墙开裂", "背景墙基层开裂，饰面起皮脱落", "铲除空鼓层，挂网后重新批刮饰面", 2200, 3200, 1.9f, 1.5f, ToolKind.Hammer,
            "裂缝宽度", "mm", 0.2f, 1.5f, 5f));
        templates.Add(new OrderTemplate("客厅", "电视线缆外露", "电视墙线缆杂乱外露，存在安全隐患", "剪除多余扎带，重新归拢线缆并加装线槽", 700, 1300, -1.9f, 1.5f, ToolKind.Scissors,
            "线缆表面温度", "℃", 28f, 60f, 95f));
        templates.Add(new OrderTemplate("客厅", "吊顶灯带脱落", "吊顶灯带卡扣老化脱落，线路外露", "更换卡扣并重新固定灯带", 1000, 1800, 0f, -2.0f, ToolKind.Drill,
            "灯带位移", "mm", 0.5f, 5f, 12f));

        // 卧室
        templates.Add(new OrderTemplate("卧室", "木门变形关不严", "木门受潮膨胀变形，闭合困难漏风", "刨修门边并调整铰链，门扇做防潮封边", 1200, 2000, -1.9f, 1.7f, ToolKind.Screwdriver,
            "门缝宽度", "mm", 1f, 4f, 10f));
        templates.Add(new OrderTemplate("卧室", "墙面返潮发霉", "外墙渗水导致内墙返潮霉变", "外墙迎水面重做防水，铲除霉变层后批耐水腻子", 2800, 3800, 1.9f, 0.5f, ToolKind.Hammer,
            "墙体含水率", "%", 8f, 18f, 35f, false));
        templates.Add(new OrderTemplate("卧室", "衣柜滑轨卡顿", "衣柜推拉门滑轨变形积尘，推拉困难", "拆下滑轨清理并重新固定，调整门扇垂直度", 600, 1200, 1.9f, 1.7f, ToolKind.Screwdriver,
            "推拉阻力", "N", 20f, 80f, 150f));
        templates.Add(new OrderTemplate("卧室", "床头插座松动", "床头插座面板松动，插拔打火", "更换面板并加固暗盒", 800, 1400, 0f, -1.7f, ToolKind.Tester,
            "接触电阻", "mΩ", 8f, 50f, 120f));

        // 卫生间
        templates.Add(new OrderTemplate("卫生间", "地漏返味", "地漏存水弯干涸失效，下水道异味返涌", "拆换防臭地漏芯，补做存水弯", 800, 1400, 0f, -1.6f, ToolKind.Wrench,
            "硫化氢浓度", "ppm", 0.5f, 5f, 15f));
        templates.Add(new OrderTemplate("卫生间", "墙面瓷砖空鼓", "淋浴区瓷砖空鼓脱层，存在脱落风险", "空鼓砖拆除重贴，基层做防水处理", 2200, 3200, -1.9f, 0.4f, ToolKind.Hammer,
            "墙面空鼓率", "%", 2f, 15f, 40f));
        templates.Add(new OrderTemplate("卫生间", "马桶底座渗水", "马桶法兰密封圈老化，底座渗水返碱", "更换法兰密封圈并打胶密封固化", 1500, 2400, 1.7f, 1.4f, ToolKind.Tape,
            "底座渗漏量", "mL/h", 3f, 20f, 60f));

        orderTimer = 2f;
    }

    // ── 维修知识手册（故障库）──────────────────────────
    private class FaultEntry
    {
        public string name;         // 故障名称
        public string symptom;      // 故障现象
        public string cause;        // 产生原因
        public string sensor;       // 传感器监测
        public string threshold;    // 异常判定（绿/黄/红）
        public string tools;        // 工具
        public string steps;        // 维修步骤
        public string selfRepair;   // 居民自修
        public string propertyRepair; // 物业/专业维修
    }

    private static readonly FaultEntry[] FaultManual =
    {
        new FaultEntry
        {
            name = "一、明装角阀/软管/水龙头漏水",
            symptom = "厨房/卫生间柜底积水、水龙头滴水、软管接口喷水，久置柜板发霉、墙面起皮。",
            cause = "橡胶密封垫老化变形；进水软管长期弯曲受热开裂；角阀阀芯磨损关不严；螺纹接口松动或生料带老化。",
            sensor = "水浸传感器（柜底/洗手盆下）检测积水液位 0~100；管道压力传感器（入户总管）测水压 MPa；流量计测无用水时流量。",
            threshold = "🟢 正常：积水 0~10\n🟡 预警：10~50 且持续 30 秒\n🔴 报警：>50 且持续 3 秒（明显积水）",
            tools = "活动扳手、老虎钳、生料带、新密封垫圈、新软管、干毛巾、水盆。",
            steps = "1. 先关水表总阀或对应角阀\n2. 干毛巾吸干积水，水盆放在渗漏点下方\n3. 观察渗漏点：软管接口滴水→扳手拧下软管螺母；龙头本体滴水→拆把手取阀芯查密封圈\n4. 垫圈变硬/变形/破损→换新垫圈\n5. 软管有裂纹/鼓包/发硬→整根换新\n6. 螺纹缠生料带：顺时针 5~6 圈，不要太厚\n7. 装回后先手拧紧，再扳手加半圈，勿用力过猛\n8. 开阀，擦干渗漏处，等 5 分钟手摸确认不漏",
            selfRepair = "明装软管、水龙头密封圈、角阀表面接口。",
            propertyRepair = "墙内预埋管渗漏、角阀锈死无法关闭、水表后主管接口问题。",
        },
        new FaultEntry
        {
            name = "二、墙内/埋地水管渗漏",
            symptom = "墙面发潮发霉、腻子鼓包，楼下天花板渗水，水费突增，踢脚线有水迹。",
            cause = "塑料水管老化变脆开裂；接口胶水老化或热熔不严；地基轻微沉降拉裂暗管；管材质量差。",
            sensor = "水浸传感器（墙根/吊顶检修口下）+ 水压传感器（入户总管）测水压 MPa。",
            threshold = "🟢 正常：水浸 0~10；水压 0.20~0.50 MPa\n🟡 预警：水浸 10~50；水压 0.15~0.18 MPa 持续 1 小时\n🔴 报警：水浸 >50；水压 <0.12 MPa（管壁破损/大量漏水）",
            tools = "活动扳手、管钳、红外热像仪、墙面开槽工具、PPR热熔器、PPR管/接头、防水卷材。",
            steps = "1. 居民先关总阀防止继续\n2. 擦干表面水迹，观察墙面水迹位置\n3. 难定位时用红外热像仪扫墙面找低温/潮湿区\n4. 专业人员用开槽工具沿疑似管路开槽\n5. 切掉破损管段，PPR管热熔重接\n6. 开阀试压检查接口\n7. 确认不漏后回填、重做防水、恢复墙面",
            selfRepair = "关总阀防止扩大、清理表面水渍、拍照记录。",
            propertyRepair = "开墙找漏点、暗管熔接、防水层修复、沉降引发的管道问题（须专业）。",
        },
        new FaultEntry
        {
            name = "三、地漏返水/排水堵塞",
            symptom = "洗衣机排水时地漏冒水，卫生间积水，水槽排水慢，下水道反味。",
            cause = "排水管油脂凝结管径变小；存水弯堵塞；主排污管堵塞；地漏老化密封不严。",
            sensor = "水浸传感器（地漏旁地面）检测积水量 0~100。",
            threshold = "🟢 正常：积水 0~10\n🟡 预警：10~50 持续 15 秒（排水不畅/少量反水）\n🔴 报警：>50 持续 3 秒（明显返水）",
            tools = "皮搋子、地漏疏通弹簧、除垢剂、热水、钳子、手套。",
            steps = "1. 先清理地面积水防滑倒\n2. 开地漏盖板清头发杂物\n3. 倒一壶约 60℃ 热水软化油垢\n4. 下水仍慢→皮搋子对准地漏口抽吸 15~20 次\n5. 无效→疏通弹簧伸入转动搅碎堵塞物\n6. 抽出后倒热水冲洗\n7. 反复返水→可能是公共排污管堵塞，报物业",
            selfRepair = "盖板杂物清理、皮搋子/弹簧疏通自家支管、地漏芯更换。",
            propertyRepair = "公共排污管堵塞、楼下检查口疏通、室外化粪池满溢。",
        },
        new FaultEntry
        {
            name = "四、水压异常（过高/过低）",
            symptom = "水龙头出水小、热水器打不着；或水压过大水管抖动、水锤声、接口易漏。",
            cause = "过低：总阀未全开、前置过滤器堵塞、公共供水不足、二次供水泵故障；过高：减压阀失效、物业调压过高、管径偏小。",
            sensor = "水压传感器（入户总水管靠近水表处）测水压 MPa。",
            threshold = "🟢 正常：0.20~0.50 MPa\n🟡 预警：<0.18 或 >0.55 MPa 持续 10 分钟\n🔴 报警：<0.12 或 >0.65 MPa 持续 1 分钟",
            tools = "活动扳手、压力表、生料带、水桶、毛刷。",
            steps = "1. 看平台水压曲线判断持续偏低还是偶发\n2. 查自家总阀是否全开（逆时针拧到头）\n3. 关总阀，拧开滤瓶清洗前置过滤器/水表滤网\n4. 仍低→压力表测入户管，区分自家/小区问题\n5. 偏高→调减压阀（逆时针降压）到约 0.30 MPa\n6. 二次供水问题→物业到泵房调变频泵",
            selfRepair = "开总阀、清洗前置过滤器滤网、调家用减压阀。",
            propertyRepair = "泵房压力设置、公共主管压力不足、市政供水故障。",
        },
        new FaultEntry
        {
            name = "五、电气过载与跳闸",
            symptom = "开多个大功率电器时回路跳闸，复位后几分钟又跳，电线发热。",
            cause = "回路大功率电器过多超断路器额定值；线径偏小；断路器老化误跳；插座接触不良高温。",
            sensor = "电气监测模块（电流互感器+温度探头）装在配电箱，测回路电流 A、线缆温度 ℃。",
            threshold = "🟢 正常：负载率 <80%，线温 <60℃\n🟡 预警：负载率 80~90%，线温 60~80℃ 持续 10 分钟\n🔴 报警：负载率 >90%，线温 >80℃ 或瞬时超额定",
            tools = "绝缘螺丝刀、电笔、万用表、钳形电流表、绝缘胶带、新断路器。",
            steps = "1. 到配电箱看哪路跳闸，拨到 OFF\n2. 断电后手背轻触出线端是否发烫\n3. 拔掉该回路所有大功率电器\n4. 万用表测回路电阻确认无短路\n5. 钳形表卡火线逐步恢复供电测电流\n6. 电流仍接近额定→大功率电器换单独回路/新增回路\n7. 断路器发烫/不灵活→换同规格断路器",
            selfRepair = "拔多余电器、复位断路器、减少同时用电。",
            propertyRepair = "换电线、增开回路、配电箱改造、换断路器（须持证电工）。",
        },
        new FaultEntry
        {
            name = "六、漏电与绝缘老化",
            symptom = "摸电器外壳有麻感，漏电保护器常跳，插座附近焦黄、有烧焦味。",
            cause = "电线绝缘老化龟裂裸铜碰金属壳；插座受潮；电器内部绝缘损坏；接地缺失。",
            sensor = "漏电互感器（零序电流互感器）装总进线回路，测漏电电流 mA。",
            threshold = "🟢 正常：漏电 <10 mA\n🟡 预警：10~30 mA 持续 5 秒\n🔴 报警：>30 mA（漏电保护器随时跳闸）",
            tools = "绝缘手套、电笔、万用表、绝缘电阻表、绝缘胶带、新插座/插头。",
            steps = "1. 戴绝缘手套断总闸\n2. 拔掉所有电器插头找漏电回路\n3. 逐一查插座：接线松动/水渍/绝缘烧焦\n4. 万用表测火线与地线电阻\n5. 插座受潮→拆下吹干换新\n6. 插头碳化→换插头或整机检查\n7. 线路老化→专业电工摇表逐段排查换线\n8. 合闸观察是否还跳",
            selfRepair = "拔故障电器、换表面插座面板、吹干受潮插座。",
            propertyRepair = "线路老化更换、接地系统修复、潮湿区域电路改造（须电工）。",
        },
        new FaultEntry
        {
            name = "七、插座/端子过热、接触不良",
            symptom = "插座发烫、插拔打火、接线端子附近变色。",
            cause = "接线螺丝热胀冷缩松动；铝铜线直接连接电化学腐蚀；插座簧片疲劳；回路长期过载。",
            sensor = "温度传感器/红外测温模块 + AFCI 电弧检测器，测温度 ℃、温升速率、危险电弧。",
            threshold = "🟢 正常：温度 <40℃\n🟡 预警：40~60℃ 或温升 >2℃/min\n🔴 报警：>60℃ 或温升 >5℃/min 或检测到危险电弧",
            tools = "电笔/万用表、螺丝刀、剥线钳、新插座、绝缘胶带。",
            steps = "1. 断对应回路断路器\n2. 电笔确认插座无电\n3. 拆面板拍照记录接线\n4. 查线皮发黑/烧焦/松动\n5. 剪掉氧化部分重新剥线\n6. 按「左零右火上接地」接线拧紧\n7. 装回面板合闸\n8. 红外测温确认温度正常后复位",
            selfRepair = "换插座面板、重新拧紧接线。",
            propertyRepair = "墙内接线盒烧熔、铝线改造、频繁跳闸/电弧报警。",
        },
        new FaultEntry
        {
            name = "八、室内烟雾/火灾隐患",
            symptom = "油烟过大、烟感报警但无明显明火；或电气短路冒烟。",
            cause = "油温过高油烟大；电气短路冒烟；杂物阴燃；烟感积灰误报。",
            sensor = "光电感烟探测器（厨房/客厅/卧室顶部）测烟雾浓度 0~100。",
            threshold = "🟢 正常：<5\n🟡 预警：5~15 持续 3 秒（油烟/灰尘/水汽）\n🔴 报警：>15 持续 3 秒（明显烟雾）",
            tools = "灭火器、湿毛巾、手电筒、螺丝刀、吸尘器。",
            steps = "1. 有明火→家人撤离、打 119、灭火器扑初期火\n2. 无明火只是油烟→关灶开油烟机通风\n3. 电气冒烟→断总闸拔插头\n4. 烟散后用吸尘器清烟感灰尘\n5. 持续报警→换传感器",
            selfRepair = "厨房通风、清洁传感器、处理阴燃杂物。",
            propertyRepair = "公共烟道/烟感系统、消防联动主机（不可自修，应急撤离）。",
        },
        new FaultEntry
        {
            name = "九、燃气泄漏",
            symptom = "闻到臭鸡蛋味，或平台燃气报警；久处室内头晕恶心。",
            cause = "软管老化龟裂/鼠咬；灶具接口胶圈老化；燃气表接口松动；热水器燃烧不充分产生一氧化碳。",
            sensor = "可燃气体传感器（厨房天花板下近燃气表）测天然气 %LEL；一氧化碳传感器测 CO ppm。",
            threshold = "🟢 正常：天然气 <5%LEL，CO <10 ppm\n🟡 预警：5~10%LEL，CO 10~30 ppm 持续 5 秒\n🔴 报警：>10%LEL，CO >30 ppm（立即处理）",
            tools = "肥皂水、毛刷、扳手、燃气专用软管、喉箍。",
            steps = "1. 立即关灶前阀和燃气总阀\n2. 严禁开灯/开关电器/打电话/穿脱毛衣\n3. 开窗通风，人撤到室外\n4. 表后软管老化→肥皂水刷接口，冒泡处即漏点\n5. 关阀，扳手松喉箍换新软管装好\n6. 开阀再肥皂水检漏确认不冒泡\n7. 漏气在表前/公共管道→撤离并打燃气抢修电话",
            selfRepair = "表后阀门到灶具间软管更换、肥皂水检漏。",
            propertyRepair = "燃气表漏气、表前公共管道、调压箱（必须燃气公司）。",
        },
        new FaultEntry
        {
            name = "十、一氧化碳 CO 超标",
            symptom = "燃气热水器燃烧不充分、烟道堵塞倒烟、室内长时间燃气灶燃烧。",
            cause = "老式直排/烟道式热水器燃烧不充分；烟道堵塞；燃气灶长时间燃烧通风差；炭火取暖。",
            sensor = "电化学 CO 传感器（厨房/热水器旁/卧室呼吸区）测 CO ppm。",
            threshold = "🟢 正常：CO <10 ppm\n🟡 预警：10~30 ppm\n🔴 报警：≥30 ppm 持续 15 分钟；≥70 ppm 立即报警",
            tools = "（居民不可自修，仅应急）",
            steps = "1. 立即开窗通风\n2. 关燃气热水器/灶\n3. 熄灭明火\n4. 人员撤到空气新鲜处\n5. 头痛/恶心/意识模糊→就医并说明可能 CO 中毒\n6. 联系厂家/燃气公司查燃烧器、烟道\n7. 公共烟道问题通知物业\n8. 平台记录 CO 曲线恢复正常后复位",
            selfRepair = "开窗通风、关闭燃气设备（应急）。",
            propertyRepair = "热水器燃烧器/烟道/排烟系统、公共烟道（专业/燃气公司）。",
        },
        new FaultEntry
        {
            name = "十一、墙体裂缝/房屋倾斜",
            symptom = "墙体斜向/窗角裂缝或持续扩大；门框变形关不上；靠墙地面凹陷。",
            cause = "材料老化；地基不均匀沉降；温度裂缝；拆墙改造改变受力。",
            sensor = "裂缝计（跨裂缝测宽度 mm）+ 倾角计（外墙顶部/承重墙转角测倾斜°）。",
            threshold = "🟢 正常：裂缝 <0.2 mm，倾斜 <0.5°\n🟡 预警：0.2~0.3 mm 且月增 <0.05 mm；倾斜 0.5~1.0°\n🔴 报警：>0.3 mm 或月增 >0.1 mm；倾斜 >1.0° 或周增 >0.1°",
            tools = "裂缝宽度卡、石膏/堵漏王、砂浆、抹子、激光水平仪、加固钢板（专业）。",
            steps = "1. 裂缝卡测宽，两端标记记录日期\n2. 表面横向裂缝→风险低；斜向/竖向贯穿→涉结构安全勿自修\n3. 表面龟裂→清理松散、浇水湿润、堵漏王/石膏填补\n4. 观察两周激光水平仪测变化\n5. 数据持续扩大/倾斜增加→报物业，结构工程师鉴定\n6. 物业措施：灌浆、粘钢、地基注浆纠偏",
            selfRepair = "表面细微龟裂、石膏补缝、记录观察。",
            propertyRepair = "承重墙裂缝、持续扩大裂缝、整体倾斜、地基沉降（须结构鉴定）。",
        },
        new FaultEntry
        {
            name = "十二、暖气系统漏水/压力异常/气堵",
            symptom = "暖气片漏水、压力忽高忽低、暖气不热（气堵）。",
            cause = "暖气片/管道腐蚀砂眼；排气阀失效；系统失水；滤网堵塞；结垢。",
            sensor = "供暖压力变送器测压力 MPa；温度探头；水浸传感器（暖气片下）。",
            threshold = "🟢 正常：压力 0.15~0.25 MPa\n🟡 预警：0.10~0.15 或 0.25~0.35 MPa\n🔴 报警：<0.10 或 >0.45 MPa 持续 1 分钟；暖气片下浸水 >10 秒",
            tools = "排气钥匙/一字螺丝刀、水桶、毛巾、扳手。",
            steps = "1. 确认系统注水运行\n2. 容器接排气阀下方\n3. 缓慢拧松排气阀\n4. 听到排气声等连续出水\n5. 立即拧紧排气阀\n6. 擦干观察是否渗水\n7. 看平台压力回绿\n8. 压力过低可轻补水，勿长期大量补水掩盖漏水",
            selfRepair = "暖气片排气、表面接口检查。",
            propertyRepair = "暖气片砂眼、主管/立管漏水、换热站/循环泵故障（供热公司）。",
        },
        new FaultEntry
        {
            name = "十三、卫生间/厨房防水失效",
            symptom = "楼下天花板渗水、地面干燥区积水、墙根发霉。",
            cause = "原防水涂料/卷材老化；管根/地漏/阴角未处理好；瓷砖空鼓开裂；改造破坏防水层。",
            sensor = "地面含水率/温湿度传感器；门口水浸检测绳；楼下天花水浸/含水率。",
            threshold = "卫生间湿度长期 >75% RH；地面干燥区水浸；楼下天花含水率 >8% 或水浸报警。",
            tools = "渗透型防水剂/美缝剂、密封胶、刷子、胶枪、手套。",
            steps = "1. 停止用水擦干地面\n2. 清理瓷砖缝灰尘旧胶\n3. 管根/阴角/瓷砖缝涂刷防水剂或补美缝\n4. 干燥后再使用\n5. 通风用除湿机\n6. 平台观察湿度是否下降",
            selfRepair = "表面密封、补美缝、管根阴角封堵。",
            propertyRepair = "楼下持续渗水、闭水试验失败、铲砖重做防水（专业防水公司）。",
        },
    };

    private bool faultManualOpen;
    private int faultManualIndex;
    private Vector2 faultManualScroll;

    private void DrawFaultManual()
    {
        if (!faultManualOpen)
        {
            return;
        }
        float width = 800f;
        float height = 540f;
        Rect rect = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
        DrawPanel(rect, new Color(0.06f, 0.08f, 0.11f, 0.98f), new Color(1f, 1f, 1f, 0.18f));
        GUI.Label(new Rect(rect.x + 20f, rect.y + 14f, width - 120f, 26f), "维修知识手册 · 检测与维修", titleStyle);

        Rect close = new Rect(rect.x + width - 84f, rect.y + 12f, 68f, 26f);
        DrawPanel(close, new Color(0.3f, 0.32f, 0.36f, 0.95f), Color.clear);
        if (GUI.Button(close, GUIContent.none, GUIStyle.none))
        {
            faultManualOpen = false;
            SetCursorLock(true);
        }
        GUI.Label(close, "关闭", cardButtonStyle);

        // 左侧：故障目录
        Rect listRect = new Rect(rect.x + 16f, rect.y + 50f, 236f, height - 64f);
        DrawPanel(listRect, new Color(1f, 1f, 1f, 0.04f), Color.clear);
        for (int i = 0; i < FaultManual.Length; i++)
        {
            Rect item = new Rect(listRect.x + 8f, listRect.y + 8f + i * 31f, listRect.width - 16f, 27f);
            if (item.Contains(Event.current.mousePosition))
            {
                Fill(item, new Color(1f, 1f, 1f, 0.08f));
            }
            if (GUI.Button(item, GUIContent.none, GUIStyle.none))
            {
                faultManualIndex = i;
                faultManualScroll = Vector2.zero;
            }
            GUI.Label(item, FaultManual[i].name, faultManualIndex == i ? cardTitleStyle : smallStyle);
        }

        // 右侧：选中故障详情（可滚动、自动换行）
        Rect detailRect = new Rect(rect.x + 264f, rect.y + 50f, width - 280f, height - 64f);
        DrawPanel(detailRect, new Color(1f, 1f, 1f, 0.04f), Color.clear);
        FaultEntry f = FaultManual[Mathf.Clamp(faultManualIndex, 0, FaultManual.Length - 1)];
        bodyStyle.wordWrap = true;
        GUILayout.BeginArea(new Rect(detailRect.x + 12f, detailRect.y + 8f, detailRect.width - 24f, detailRect.height - 16f));
        faultManualScroll = GUILayout.BeginScrollView(faultManualScroll, GUILayout.Width(detailRect.width - 24f), GUILayout.Height(detailRect.height - 16f));
        GUILayout.Label("【故障现象】\n" + f.symptom, bodyStyle);
        GUILayout.Label("【产生原因】\n" + f.cause, bodyStyle);
        GUILayout.Label("【传感器监测】\n" + f.sensor, bodyStyle);
        GUILayout.Label("【异常判定】\n" + f.threshold, bodyStyle);
        GUILayout.Label("【维修工具】\n" + f.tools, bodyStyle);
        GUILayout.Label("【维修步骤】\n" + f.steps, bodyStyle);
        GUILayout.Label("【居民自修】\n" + f.selfRepair, bodyStyle);
        GUILayout.Label("【须物业/专业】\n" + f.propertyRepair, bodyStyle);
        GUILayout.EndScrollView();
        GUILayout.EndArea();

        GUILayout.Space(0f);   // 保证 GUILayout 与 GUI 混合时布局正确
    }

    private void UpdateOrderSpawning()
    {
        if (!introDone)
        {
            return;
        }

        if (orderTimer > 0f)
        {
            orderTimer -= Time.deltaTime;
            return;
        }

        // 不再凭空生成工单：由户主上门告知，玩家听完才接单
        if (CountActive() + homeowners.Count < MaxActiveOrders && TrySpawnHomeowner())
        {
            orderTimer = Random.Range(OrderIntervalMin, OrderIntervalMax);
        }
        else
        {
            orderTimer = 1.5f; // 满单或无可派位置，稍后重试
        }
    }

    // ── 户主上门告知 ──────────────────────────────────────
    private class Homeowner
    {
        public CharacterRig rig;
        public GameObject bubble;
        public Room room;
        public OrderTemplate template;
        public int phase;      // 0 走向前台 1 登记 2 离开 3 找玩家对话 4 找同事沟通
        public Colleague target;   // 要找的同事
        public float timer;
        public float stuck;    // 被墙挡住累计时长
        public bool moving;
        public int routeIndex; // 进门前按路点走，避免撞墙
    }

    private readonly List<Homeowner> homeowners = new List<Homeowner>();

    // 进公司大门的路点：门外对齐门洞 → 穿过门 → 前台
    private static readonly Vector3[] homeownerRoute =
    {
        new Vector3(-16.4f, 0f, -9.0f),
        new Vector3(-16.2f, 0f, -5.4f),
    };

    // 办公室同事（可对话）
    private class Colleague
    {
        public string name;
        public CharacterRig rig;
        public string[] lines;
        public Vector3 seat;      // 工位坐标（坐下时的位置）
        public float seatYaw;     // 工位朝向（坐下时面朝工位）
        public int state;         // 0 在岗 1 下班离场 2 已回家 3 返岗途中
        public float walkTimer;   // 走路超时兜底
    }
    private readonly List<Colleague> colleagues = new List<Colleague>();
    private Colleague activeColleague;

    private bool TrySpawnHomeowner()
    {
        // 选一个"位置上还没有活跃工单"的房间×模板组合
        List<Room> candidateRooms = new List<Room>();
        List<OrderTemplate> candidateTemplates = new List<OrderTemplate>();

        for (int r = 0; r < rooms.Count; r++)
        {
            Room room = rooms[r];
            if (room.type == "公司" || room.type == "宿舍")
            {
                continue;
            }
            if (room.building > 0 && !IsBuildingUnlocked(room.building))
            {
                continue;   // 未解锁的楼栋不派单
            }
            for (int t = 0; t < templates.Count; t++)
            {
                if (templates[t].roomType != room.type)
                {
                    continue;
                }
                Vector3 site = room.center + templates[t].offset;
                site.y = 0f;
                if (HasActiveOrderAt(site) || HasHomeownerFor(room, templates[t]))
                {
                    continue;
                }
                candidateRooms.Add(room);
                candidateTemplates.Add(templates[t]);
            }
        }

        if (candidateRooms.Count == 0)
        {
            return false;
        }

        // 优先派当前工具能修的活，避免玩家接不到单也没钱买工具
        List<Room> pickRooms = candidateRooms;
        List<OrderTemplate> pickTemplates = candidateTemplates;
        if (Random.value < 0.78f)
        {
            List<Room> ownedRooms = new List<Room>();
            List<OrderTemplate> ownedTemplates = new List<OrderTemplate>();
            for (int i = 0; i < candidateTemplates.Count; i++)
            {
                if (HasTool(candidateTemplates[i].tool))
                {
                    ownedRooms.Add(candidateRooms[i]);
                    ownedTemplates.Add(candidateTemplates[i]);
                }
            }
            if (ownedRooms.Count > 0)
            {
                pickRooms = ownedRooms;
                pickTemplates = ownedTemplates;
            }
        }

        int pick = Random.Range(0, pickRooms.Count);

        // 40% 直接打电话预约，不出现 NPC
        if (Random.value < 0.4f)
        {
            RegisterOrder(pickRooms[pick], pickTemplates[pick], "电话预约");
            return true;
        }

        // 其余：本人到公司前台登记
        Vector3 spawn = new Vector3(-16.4f, GroundLevel, -11f);
        float yaw = Mathf.Atan2(receptionPoint.x - spawn.x, receptionPoint.z - spawn.z) * Mathf.Rad2Deg;
        Color[] coats =
        {
            new Color(0.55f, 0.42f, 0.62f),
            new Color(0.35f, 0.5f, 0.42f),
            new Color(0.6f, 0.45f, 0.35f),
            new Color(0.4f, 0.45f, 0.6f),
        };
        CharacterRig rig = BuildCharacterModel("户主", spawn, yaw, coats[Random.Range(0, coats.Length)], new Color(0.85f, 0.68f, 0.52f));

        homeowners.Add(new Homeowner
        {
            rig = rig,
            room = pickRooms[pick],
            template = pickTemplates[pick],
            phase = 0
        });
        ShowToast("有户主上门了，去听听是什么问题", 4f);
        return true;
    }

    private bool HasHomeownerFor(Room room, OrderTemplate template)
    {
        for (int i = 0; i < homeowners.Count; i++)
        {
            if (homeowners[i].room == room && homeowners[i].template == template)
            {
                return true;
            }
        }
        return false;
    }

    // 公司前台位置：户主来这里登记，而不是追着玩家跑
    private readonly Vector3 receptionPoint = new Vector3(-13.2f, GroundLevel, -4.3f);

    // 登记预约：生成带时间的工单
    private void RegisterOrder(Room room, OrderTemplate template, string channel)
    {
        Vector3 site = room.center + template.offset;
        site.y = 0f;

        // 造价按材料清单与人工工时核算，不再是随机数
        int cost = QuoteCost(template.title);
        Order order = new Order
        {
            id = ++orderSerial,
            title = template.title,
            room = room.name,
            cause = template.cause,
            plan = template.plan,
            cost = cost,
            site = site,
            state = OrderState.Pending,
            requiredTool = template.tool,
            selfRepairable = template.selfRepairable,
            sensorName = template.sensorName,
            sensorUnit = template.sensorUnit,
            sensorNormal = template.sensorNormal,
            sensorAlarm = template.sensorAlarm,
            sensorMax = template.sensorMax,
            // 报警初值：高于报警阈值 15%~45%，体现"已超标"
            sensorValue = template.sensorAlarm * Random.Range(1.15f, 1.45f),
            alarmTime = gameTime,
            schedDay = DayIndex + (Random.value < 0.5f ? 0 : 1),
            schedHour = Random.Range(9, 18)
        };
        BuildOrderMarker(order);
        orders.Add(order);
        PlayNotify();
        ShowToast(channel + " 新工单 " + order.Code + " · " + order.room + " · " + order.title + "　预约 " + order.ScheduleText, 6f);
    }

    private void UpdateHomeowners()
    {
        for (int i = homeowners.Count - 1; i >= 0; i--)
        {
            Homeowner owner = homeowners[i];
            if (owner.rig.root == null)
            {
                homeowners.RemoveAt(i);
                continue;
            }

            if (owner.phase == 0)
            {
                // 按路点走：大门外 → 门洞 → 前台。直线过去会撞在门边的墙上
                Vector3 target = owner.routeIndex < homeownerRoute.Length
                    ? new Vector3(homeownerRoute[owner.routeIndex].x, owner.rig.root.position.y, homeownerRoute[owner.routeIndex].z)
                    : receptionPoint;

                Vector3 delta = target - owner.rig.root.position;
                delta.y = 0f;
                if (delta.magnitude <= 0.7f && owner.routeIndex < homeownerRoute.Length)
                {
                    owner.routeIndex++;
                    delta = target - owner.rig.root.position;
                }

                bool atReception = Distance2D(owner.rig.root.position, receptionPoint) <= 1.1f;
                if (atReception || owner.stuck > 8f)
                {
                    owner.phase = 1;
                    owner.moving = false;
                    owner.timer = 2.5f;   // 登记中
                    FaceReception(owner);
                }
                else
                {
                    Vector3 step = delta.normalized * 2.4f * Time.deltaTime;
                    Vector3 next = owner.rig.root.position + step;

                    bool moved = false;
                    if (!Collides(next, true))
                    {
                        owner.rig.root.position = next;
                        moved = true;
                    }
                    else
                    {
                        // 分离轴滑动，让他贴着墙找路
                        Vector3 xOnly = new Vector3(next.x, owner.rig.root.position.y, owner.rig.root.position.z);
                        if (!Collides(xOnly, true))
                        {
                            owner.rig.root.position = xOnly;
                            moved = true;
                        }
                        else
                        {
                            Vector3 zOnly = new Vector3(owner.rig.root.position.x, owner.rig.root.position.y, next.z);
                            if (!Collides(zOnly, true))
                            {
                                owner.rig.root.position = zOnly;
                                moved = true;
                            }
                        }
                    }
                    owner.stuck = moved ? 0f : owner.stuck + Time.deltaTime;
                    owner.moving = moved;

                    if (delta.sqrMagnitude > 0.01f)
                    {
                        owner.rig.root.rotation = Quaternion.Slerp(owner.rig.root.rotation, Quaternion.LookRotation(delta), Time.deltaTime * 6f);
                    }
                }
            }
            else if (owner.phase == 1)
            {
                // 在前台登记
                owner.moving = false;
                FaceReception(owner);
                owner.timer -= Time.deltaTime;
                if (owner.timer <= 0f)
                {
                    FileReport(owner);
                    // 登记后主动找人沟通：玩家在公司内就找玩家，否则找在场的同事
                    bool playerInside = Distance2D(playerPosition, owner.rig.root.position) < 12f;
                    if (playerInside && dialogueIndex < 0 && talkTarget == null && !dialogueJustEnded)
                    {
                        owner.phase = 3;
                        owner.timer = 90f;
                        StartConversation(owner);
                    }
                    else
                    {
                        Colleague mate = NearestColleague(owner.rig.root.position);
                        if (mate != null)
                        {
                            owner.phase = 4;
                            owner.timer = 6f;
                            owner.target = mate;
                            ShowToast("业主正在向 " + mate.name + " 说明情况", 3.5f);
                        }
                        else
                        {
                            owner.phase = 2;
                        }
                    }
                }
            }
            else if (owner.phase == 3)
            {
                // 与玩家对话中：等 EndConversation 把 phase 切到 2
                owner.moving = false;
                FacePlayer(owner, 5f);
                owner.timer -= Time.deltaTime;
                if (owner.timer <= 0f)
                {
                    owner.phase = 2;
                }
            }
            else if (owner.phase == 4)
            {
                // 走到同事工位旁沟通，谈完再离开
                if (owner.target == null || owner.target.rig == null)
                {
                    owner.phase = 2;
                    owner.moving = false;
                }
                else
                {
                    Vector3 target = owner.target.rig.root.position;
                    Vector3 delta = target - owner.rig.root.position;
                    delta.y = 0f;

                    if (delta.magnitude <= 1.7f)
                    {
                        owner.moving = false;
                        owner.timer -= Time.deltaTime;
                        // 面朝同事
                        Vector3 look = target - owner.rig.root.position;
                        look.y = 0f;
                        if (look.sqrMagnitude > 0.01f)
                        {
                            owner.rig.root.rotation = Quaternion.Slerp(owner.rig.root.rotation,
                                Quaternion.LookRotation(look), Time.deltaTime * 5f);
                        }
                        if (owner.timer <= 0f)
                        {
                            owner.phase = 2;
                        }
                    }
                    else
                    {
                        owner.moving = true;
                        Vector3 next = owner.rig.root.position + delta.normalized * 2.4f * Time.deltaTime;
                        if (!Collides(next, true))
                        {
                            owner.rig.root.position = next;
                        }
                        else
                        {
                            Vector3 xOnly = new Vector3(next.x, owner.rig.root.position.y, owner.rig.root.position.z);
                            if (!Collides(xOnly, true))
                            {
                                owner.rig.root.position = xOnly;
                            }
                            else
                            {
                                Vector3 zOnly = new Vector3(owner.rig.root.position.x, owner.rig.root.position.y, next.z);
                                if (!Collides(zOnly, true))
                                {
                                    owner.rig.root.position = zOnly;
                                }
                                else
                                {
                                    owner.stuck += Time.deltaTime;
                                }
                            }
                        }
                        owner.rig.root.rotation = Quaternion.Slerp(owner.rig.root.rotation,
                            Quaternion.LookRotation(delta), Time.deltaTime * 6f);
                        if (owner.stuck > 6f)
                        {
                            owner.phase = 2;   // 兜底：走不过去就直接离开，避免原地打转
                        }
                    }
                }
            }
            else
            {
                // 回家：先走到自家门外对齐门洞，再径直进屋
                owner.moving = true;
                Vector3 home = owner.room.doorPoint;
                float y = owner.rig.root.position.y;

                bool alignedWithDoor = Mathf.Abs(owner.rig.root.position.x - home.x) < 1.0f;
                Vector3 target = alignedWithDoor
                    ? home
                    : new Vector3(home.x, y, home.z - 4.6f);   // 自家门外落客点，先横向对齐

                Vector3 delta = target - owner.rig.root.position;
                delta.y = 0f;
                if (delta.sqrMagnitude < 0.01f)
                {
                    delta = Vector3.forward;
                }

                Vector3 step = delta.normalized * 2.6f * Time.deltaTime;
                Vector3 next = owner.rig.root.position + step;
                if (!Collides(next, true))
                {
                    owner.rig.root.position = next;
                }
                else
                {
                    Vector3 xOnly = new Vector3(next.x, y, owner.rig.root.position.z);
                    if (!Collides(xOnly, true))
                    {
                        owner.rig.root.position = xOnly;
                    }
                    else
                    {
                        Vector3 zOnly = new Vector3(owner.rig.root.position.x, y, next.z);
                        if (!Collides(zOnly, true))
                        {
                            owner.rig.root.position = zOnly;
                        }
                        else
                        {
                            owner.stuck += Time.deltaTime;
                        }
                    }
                }

                owner.rig.root.rotation = Quaternion.Slerp(owner.rig.root.rotation, Quaternion.LookRotation(delta), Time.deltaTime * 6f);

                // 进了自家门（玩家看不见屋内，消失很自然），或长时间回不去
                bool arrived = Mathf.Abs(owner.rig.root.position.z - home.z) < 0.9f && alignedWithDoor;
                if (arrived || owner.stuck > 12f)
                {
                    Destroy(owner.rig.root.gameObject);
                    homeowners.RemoveAt(i);
                }
            }

            AnimateRig(owner.rig, owner.moving, 1f);
        }
    }

    // 找距离某点最近的在场同事
    private Colleague NearestColleague(Vector3 from)
    {
        Colleague best = null;
        float nearest = float.MaxValue;
        for (int i = 0; i < colleagues.Count; i++)
        {
            Colleague c = colleagues[i];
            if (c.rig == null || c.rig.root == null || !c.rig.root.gameObject.activeSelf)
            {
                continue;
            }
            float d = Distance2D(from, c.rig.root.position);
            if (d < nearest)
            {
                nearest = d;
                best = c;
            }
        }
        return best;
    }

    private void FacePlayer(Homeowner owner, float speed)
    {
        Vector3 look = playerPosition - owner.rig.root.position;
        look.y = 0f;
        if (look.sqrMagnitude > 0.01f)
        {
            owner.rig.root.rotation = Quaternion.Slerp(owner.rig.root.rotation, Quaternion.LookRotation(look), Time.deltaTime * speed);
        }
    }

    private void Billboard(GameObject target)
    {
        Vector3 look = target.transform.position - viewCamera.transform.position;
        if (look.sqrMagnitude > 0.01f)
        {
            target.transform.rotation = Quaternion.LookRotation(look, Vector3.up);
        }
    }

    // 户主说明情况 → 生成工单 + 头顶气泡
    private void FileReport(Homeowner owner)
    {
        RegisterOrder(owner.room, owner.template, "上门登记");
    }

    private void FaceReception(Homeowner owner)
    {
        Vector3 look = receptionPoint - owner.rig.root.position;
        look.y = 0f;
        if (look.sqrMagnitude > 0.01f)
        {
            owner.rig.root.rotation = Quaternion.LookRotation(look);
        }
    }

    private GameObject BuildBubble(Vector3 position, string text)
    {
        GameObject bubble = new GameObject("Speech Bubble");
        bubble.transform.SetParent(transform, false);
        bubble.transform.position = position;

        Material board = MakeMaterial(new Color(0.98f, 0.97f, 0.93f), 0f, 0.4f);
        DecoPart(PrimitiveType.Cube, "Bubble Board", bubble.transform, Vector3.zero, new Vector3(4.9f, 0.74f, 0.05f), Quaternion.identity, board);
        DecoPart(PrimitiveType.Cube, "Bubble Tail", bubble.transform, new Vector3(0f, -0.48f, 0f), new Vector3(0.16f, 0.26f, 0.05f), Quaternion.Euler(0f, 0f, 32f), board);

        GameObject labelObject = new GameObject("Bubble Text");
        labelObject.transform.SetParent(bubble.transform, false);
        labelObject.transform.localPosition = new Vector3(0f, 0f, -0.05f);
        TextMesh mesh = labelObject.AddComponent<TextMesh>();
        mesh.font = UiFont;
        mesh.text = text;
        mesh.fontSize = 64;
        // 实测公式：每字宽度 ≈ characterSize × 6.4。按字数自适应，保证文字始终放得进气泡
        float perChar = 4.3f / Mathf.Max(1, text.Length);
        mesh.characterSize = Mathf.Clamp(perChar / 6.4f, 0.016f, 0.04f);
        mesh.anchor = TextAnchor.MiddleCenter;
        mesh.alignment = TextAlignment.Center;
        mesh.color = new Color(0.15f, 0.15f, 0.18f);
        Renderer renderer = labelObject.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = GetLabelMaterial(new Color(0.15f, 0.15f, 0.18f));
        }
        return bubble;
    }

    private bool TrySpawnOrder()
    {
        // 收集所有"房间 × 该房型问题模板"中，位置上还没有活跃工单的候选
        List<Room> candidateRooms = new List<Room>();
        List<OrderTemplate> candidateTemplates = new List<OrderTemplate>();

        for (int r = 0; r < rooms.Count; r++)
        {
            Room room = rooms[r];
            if (room.type == "公司")
            {
                continue;
            }
            if (room.building > 0 && !IsBuildingUnlocked(room.building))
            {
                continue;   // 未解锁的楼栋不派单
            }
            for (int t = 0; t < templates.Count; t++)
            {
                if (templates[t].roomType != room.type)
                {
                    continue;
                }
                Vector3 site = room.center + templates[t].offset;
                site.y = 0f;
                if (HasActiveOrderAt(site))
                {
                    continue;
                }
                candidateRooms.Add(room);
                candidateTemplates.Add(templates[t]);
            }
        }

        if (candidateRooms.Count == 0)
        {
            return false;
        }

        int pick = Random.Range(0, candidateRooms.Count);
        Room target = candidateRooms[pick];
        OrderTemplate template = candidateTemplates[pick];
        Vector3 spot = target.center + template.offset;
        spot.y = 0f;

        int cost = Mathf.RoundToInt(Random.Range(template.costMin, template.costMax + 1) / 100f) * 100;

        Order order = new Order
        {
            id = ++orderSerial,
            title = template.title,
            room = target.name,
            cause = template.cause,
            plan = template.plan,
            cost = cost,
            site = spot,
            state = OrderState.Pending,
            selfRepairable = template.selfRepairable
        };

        BuildOrderMarker(order);
        orders.Add(order);
        ShowToast("新工单 " + order.Code + " · " + order.room + " · " + order.title + "（自动接单）", 4.5f);
        return true;
    }

    private bool HasActiveOrderAt(Vector3 spot)
    {
        for (int i = 0; i < orders.Count; i++)
        {
            if (orders[i].state == OrderState.Fixed)
            {
                continue;
            }
            if (Distance2D(orders[i].site, spot) < 1.2f)
            {
                return true;
            }
        }
        return false;
    }

    private void BuildOrderMarker(Order order)
    {
        GameObject marker = new GameObject("Marker " + order.Code);
        marker.transform.SetParent(transform, false);
        marker.transform.position = new Vector3(order.site.x, MarkerHeight, order.site.z);

        // 数据牌：深色底板 + 状态色描边（替代原来的感叹号，直接体现"数字孪生"）
        Material boardMat = MakeMaterial(new Color(0.05f, 0.09f, 0.12f), 0.1f, 0.4f);
        GameObject board = MakePrimitive(PrimitiveType.Cube, "Tag Board", marker.transform, new Vector3(0f, 0f, 0.03f), new Vector3(2.5f, 0.8f, 0.05f), Quaternion.identity, boardMat);
        GameObject edge = MakePrimitive(PrimitiveType.Cube, "Tag Edge", marker.transform, new Vector3(0f, 0f, 0.06f), new Vector3(2.58f, 0.88f, 0.02f), Quaternion.identity, stateMaterials[0]);

        GameObject labelObject = new GameObject("Tag Text");
        labelObject.transform.SetParent(marker.transform, false);
        labelObject.transform.localPosition = new Vector3(0f, 0f, 0f);
        TextMesh mesh = labelObject.AddComponent<TextMesh>();
        mesh.font = UiFont;
        mesh.fontSize = 64;
        mesh.characterSize = 0.075f;
        mesh.anchor = TextAnchor.MiddleCenter;
        mesh.alignment = TextAlignment.Center;
        mesh.lineSpacing = 1.0f;
        mesh.color = stateColors[0];
        Renderer labelRenderer = labelObject.GetComponent<Renderer>();
        if (labelRenderer != null)
        {
            labelRenderer.sharedMaterial = GetLabelMaterial(new Color(0.92f, 0.96f, 1f));
        }

        GameObject ring = CreateCylinder("Ring", new Vector3(order.site.x, 0.02f, order.site.z), 0.17f, 0.012f, Quaternion.identity, stateMaterials[0]);
        GameObject beam = CreateCylinder("Beam", new Vector3(order.site.x, MarkerHeight * 0.5f, order.site.z), 0.009f, MarkerHeight, Quaternion.identity, stateMaterials[0]);
        ring.transform.SetParent(marker.transform, true);
        beam.transform.SetParent(marker.transform, true);

        order.marker = marker;
        order.tag = mesh;
        order.renderers = new[]
        {
            edge.GetComponent<Renderer>(),
            ring.GetComponent<Renderer>(),
            beam.GetComponent<Renderer>(),
            board.GetComponent<Renderer>()
        };
    }

    // ── 交互与维修 ────────────────────────────────────────
    private void DetectInteraction()
    {
        if (repairingOrder != null || dialogueIndex >= 0 || dialogueJustEnded)
        {
            activeOrder = null;
            activeColleague = null;
            return;
        }

        // 床边按 E 睡觉（参考沙盒游戏）
        if (Input.GetKeyDown(KeyCode.E) && Distance2D(playerPosition, sleepPoint) < 2.3f)
        {
            StartSleep();
            return;
        }

        Order nearest = null;
        float best = InteractDistance;
        for (int i = 0; i < orders.Count; i++)
        {
            if (orders[i].state != OrderState.Pending)
            {
                continue;
            }
            float d = Distance2D(playerPosition, orders[i].site);
            if (d < best)
            {
                best = d;
                nearest = orders[i];
            }
        }
        activeOrder = nearest;

        // 附近有同事就优先提示聊天
        activeColleague = null;
        float best2 = 2.3f;
        for (int i = 0; i < colleagues.Count; i++)
        {
            if (colleagues[i].rig == null || !colleagues[i].rig.root.gameObject.activeSelf)
            {
                continue;
            }
            float d = Distance2D(playerPosition, colleagues[i].rig.root.position);
            if (d < best2)
            {
                best2 = d;
                activeColleague = colleagues[i];
            }
        }
        if (activeColleague != null && Input.GetKeyDown(KeyCode.E))
        {
            StartColleagueChat(activeColleague);
            return;
        }

        // 附近有工头，按 E 主动听嘱托
        if (bossRig != null && bossRig.root != null
            && Distance2D(playerPosition, bossRig.root.position) < 2.6f && Input.GetKeyDown(KeyCode.E))
        {
            StartBossBriefing();
            return;
        }

    }

    // 左键点击施工：独立处理，不能放在 DetectInteraction 里（那里维修中会整体提前返回）
    private void HandleRepairInput()
    {
        if (!cursorLocked || dialogueIndex >= 0 || dialogueJustEnded)
        {
            return;
        }
        if (!Input.GetMouseButtonDown(0))
        {
            return;
        }
        if (repairingOrder != null)
        {
            AdvanceRepair();
        }
        else if (activeOrder != null)
        {
            if (IsNight)
            {
                ShowToast("天黑了，收工吧 —— 按 R 回驻地休息，明天再干", 4f);
                return;
            }

            // 必须用对工具才能动手
            ToolKind need = activeOrder.requiredTool;
            if (tools[currentTool].kind != need)
            {
                if (!HasTool(need))
                {
                    ShowToast("修「" + activeOrder.title + "」需要【" + ToolName(need) + "】，你还没这件工具，先去商店买", 5f);
                }
                else
                {
                    ShowToast("修「" + activeOrder.title + "」得用【" + ToolName(need) + "】，按 Q / 滚轮 换工具", 4f);
                }
                return;
            }

            StartRepair(activeOrder);
            AdvanceRepair();
        }
    }

    private void AdvanceRepair()
    {
        repairClicks++;
        toolStrike = 1f;
        if (repairingOrder != null)
        {
            EmitWorkEffect(repairingOrder.site);
        }
        repairingOrder.repairProgress = Mathf.Clamp01((float)repairClicks / RepairClicks);
        if (repairClicks >= RepairClicks)
        {
            CompleteRepair();
        }
    }

    private void CompleteRepair()
    {
        int unlockedBefore = UnlockedBuildingCount();
        repairingOrder.repairProgress = 1f;
        repairingOrder.state = OrderState.Fixed;
        AddIncome(repairingOrder.cost);
        PlayNotify();
        ShowToast("工单完成 " + repairingOrder.Code + " · " + repairingOrder.room + " " + repairingOrder.title + "（业主支付 ¥" + repairingOrder.cost.ToString("N0") + "）", 5f);

        int unlockedAfter = UnlockedBuildingCount();
        if (unlockedAfter > unlockedBefore)
        {
            ShowToast("晋升为「" + RankTitle() + "」！解锁 " + unlockedAfter + "号楼", 6f);
        }

        repairingOrder = null;
        activeOrder = null;
        repairClicks = 0;
        SaveGame();
    }

    private void StartRepair(Order order)
    {
        repairingOrder = order;
        order.state = OrderState.Repairing;
        order.repairProgress = 0f;
        repairClicks = 0;

        Vector3 direction = order.site - playerPosition;
        direction.y = 0f;
        if (direction.sqrMagnitude > 0.001f)
        {
            player.transform.forward = direction.normalized;
        }

        ShowToast("开始维修 " + order.Code + " · 连续点击左键施工（共 " + RepairClicks + " 次）", 4f);
    }

    private void UpdateRepair()
    {
        // 工具挥动/钻孔的余韵随时间衰减
        if (toolStrike > 0f)
        {
            toolStrike = Mathf.Max(0f, toolStrike - Time.deltaTime * 4.5f);
        }
    }

    private void UpdateMarkers()
    {
        for (int i = 0; i < orders.Count; i++)
        {
            Order order = orders[i];
            if (order.marker == null)
            {
                continue;
            }

            Material material = stateMaterials[(int)order.state];
            for (int r = 0; r < order.renderers.Length; r++)
            {
                if (order.renderers[r] != null)
                {
                    order.renderers[r].sharedMaterial = material;
                }
            }

            // 数据牌实时文字：传感器名 / 当前读数 / 状态
            if (order.tag != null)
            {
                bool over = order.state != OrderState.Fixed && order.sensorValue >= order.sensorAlarm;
                string mark = order.state == OrderState.Fixed ? "OK" : (over ? "超标" : "预警");
                order.tag.text = order.sensorName + "\n" + order.sensorValue.ToString("F1") + " " + order.sensorUnit + "  " + mark
                    + (OrderSelfRepairable(order) ? "" : " ·须物业");
                order.tag.color = stateColors[(int)order.state];
            }

            float speed = order.state == OrderState.Repairing ? 6f : 3f;
            float pulse = 1f + Mathf.Sin(Time.time * speed + i) * 0.07f;
            if (order == activeOrder || order == repairingOrder)
            {
                pulse += 0.15f;
            }
            order.marker.transform.localScale = Vector3.one * pulse;

            Vector3 look = order.marker.transform.position - viewCamera.transform.position;
            if (look.sqrMagnitude > 0.01f)
            {
                order.marker.transform.rotation = Quaternion.LookRotation(look, Vector3.up);
            }
        }
    }

    private int CountActive()
    {
        int count = 0;
        for (int i = 0; i < orders.Count; i++)
        {
            if (orders[i].state != OrderState.Fixed)
            {
                count++;
            }
        }
        return count;
    }

    private void UpdateCurrentRoom()
    {
        currentRoomName = "室外";
        for (int i = 0; i < rooms.Count; i++)
        {
            if (rooms[i].Contains(playerPosition))
            {
                currentRoomName = rooms[i].name;
                break;
            }
        }
    }

    private float Distance2D(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private int CountFixed()
    {
        int count = 0;
        for (int i = 0; i < orders.Count; i++)
        {
            if (orders[i].state == OrderState.Fixed)
            {
                count++;
            }
        }
        return count;
    }

    // 待办在前、最近完工在后，用于渲染工单卡片
    private List<Order> BuildDisplayList()
    {
        List<Order> list = new List<Order>();
        for (int i = 0; i < orders.Count; i++)
        {
            if (orders[i].state != OrderState.Fixed)
            {
                list.Add(orders[i]);
            }
        }

        List<Order> done = new List<Order>();
        for (int i = 0; i < orders.Count; i++)
        {
            if (orders[i].state == OrderState.Fixed)
            {
                done.Add(orders[i]);
            }
        }
        for (int i = done.Count - 1; i >= 0 && done.Count - i <= MaxVisibleDone; i--)
        {
            list.Add(done[i]);
        }
        return list;
    }

    private void ShowToast(string text, float duration = 4f)
    {
        toastText = text;
        toastTimer = duration;
    }

    // ── 界面 ──────────────────────────────────────────────
    private Rect BudgetRect { get { return new Rect(16f, 16f, 320f, 140f); } }
    private Rect TaskListRect
    {
        get
        {
            // 展开=固定高度页面内部滚动；折叠=只显示任务栏(1-2条)
            float height = taskListExpanded ? 400f : 205f;
            return new Rect(Screen.width - 348f, 16f, 332f, height);
        }
    }
    private Rect PromptRect { get { return new Rect(16f, Screen.height - 152f, 430f, 112f); } }
    private Rect ToolChipRect { get { return new Rect(16f, Screen.height - 196f, 340f, 36f); } }
    private Rect BagRect { get { return new Rect(16f, 146f, 300f, 56f + tools.Count * 32f); } }
    private Rect ShopRect
    {
        get
        {
            int rows = Mathf.Max(tools.Count, outfits.Count);
            // 与工具包同一列、固定在屏幕最左侧，收起后完全隐藏（参考日历浮层）
            return new Rect(16f, 146f, 330f, 122f + rows * 32f);
        }
    }
    private Rect HintRect { get { return new Rect(0f, Screen.height - 30f, Screen.width, 30f); } }
    private Rect LoginRect { get { return new Rect((Screen.width - 420f) * 0.5f, (Screen.height - 330f) * 0.5f, 420f, 330f); } }

    private void OnGUI()
    {
        EnsureStyles();
        DrawMinimap();
        DrawBudgetPanel();
        DrawTaskList();
        DrawBag();
        DrawShop();
        DrawAlmanac();
        DrawTwinPanel();
        DrawGuide();
        DrawToolChip();
        DrawPromptPanel();
        DrawDialogue();
        DrawHintBar();
        DrawToast();
        DrawStartOverlay();
        DrawTransition();
        DrawLogin();
        DrawLobby();
        DrawChat();
        DrawFaultManual();
        DrawStartError();
    }

    // ── 小地图（M 键切换大/小）────────────────────────────
    private Rect MinimapRect
    {
        get
        {
            return minimapLarge
                ? new Rect(Screen.width - 512f, Screen.height - 314f, 496f, 244f)
                : new Rect(Screen.width - 312f, Screen.height - 180f, 296f, 128f);
        }
    }

    private Vector2 WorldToMap(Vector3 world)
    {
        Rect rect = MinimapRect;
        float u = Mathf.InverseLerp(WorldMinX, WorldMaxX, world.x);
        float v = Mathf.InverseLerp(WorldMinZ, WorldMaxZ, world.z);
        // 世界 +Z 朝上，映射到地图上方
        return new Vector2(rect.x + u * rect.width, rect.y + (1f - v) * rect.height);
    }

    private void DrawMinimap()
    {
        Rect rect = MinimapRect;
        DrawPanel(rect, panelFill, panelBorder);
        GUI.Label(new Rect(rect.x + 12f, rect.y + 6f, 200f, 22f), "现场平面图", cardTitleStyle);
        GUI.Label(new Rect(rect.x + rect.width - 90f, rect.y + 8f, 78f, 20f), minimapLarge ? "[M] 缩小" : "[M] 放大", smallStyle);

        Rect map = new Rect(rect.x + 12f, rect.y + 28f, rect.width - 24f, rect.height - 40f);

        // 房间底色
        for (int i = 0; i < rooms.Count; i++)
        {
            Room room = rooms[i];
            Vector2 a = WorldToMap(new Vector3(room.xMin, 0f, room.zMax));
            Vector2 b = WorldToMap(new Vector3(room.xMax, 0f, room.zMin));
            Rect r = new Rect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
            bool company = room.name == "装修公司";
            Fill(r, company ? new Color(0.22f, 0.42f, 0.58f, 0.55f) : new Color(1f, 1f, 1f, 0.10f));
            // 边框
            Color outline = new Color(1f, 1f, 1f, 0.22f);
            Fill(new Rect(r.x, r.y, r.width, 1f), outline);
            Fill(new Rect(r.x, r.yMax, r.width, 1f), outline);
            Fill(new Rect(r.x, r.y, 1f, r.height), outline);
            Fill(new Rect(r.xMax, r.y, 1f, r.height), outline);
        }

        // 工单位置
        for (int i = 0; i < orders.Count; i++)
        {
            Vector2 p = WorldToMap(orders[i].site);
            Fill(new Rect(p.x - 3f, p.y - 3f, 6f, 6f), stateColors[(int)orders[i].state]);
        }

        // 玩家（带朝向的三角）
        Vector2 me = WorldToMap(playerPosition);
        Fill(new Rect(me.x - 3.5f, me.y - 3.5f, 7f, 7f), new Color(1f, 1f, 1f, 0.95f));
        Vector2 ahead = WorldToMap(playerPosition + Quaternion.Euler(0f, lookYaw, 0f) * Vector3.forward * 2f);
        Vector2 dir = (ahead - me).normalized;
        Fill(new Rect(me.x + dir.x * 6f - 1.5f, me.y + dir.y * 6f - 1.5f, 3f, 3f), fixedColor);

        GUI.Label(new Rect(rect.x + 12f, rect.yMax - 22f, rect.width - 24f, 18f), "白点=你　彩点=工单　当前：" + currentRoomName, smallStyle);
    }

    // ── 对话 ──────────────────────────────────────────────
    // 原神风格对话：上下黑边 + 底部对话框 + 逐字显示
    private void DrawDialogue()
    {
        if (dialogueIndex < 0 || dialogueIndex >= dialogue.Count)
        {
            return;
        }

        DialogueLine line = dialogue[dialogueIndex];
        int visible = VisibleCharCount();
        bool complete = visible >= line.text.Length;
        string shown = complete ? line.text : line.text.Substring(0, visible);

        // 电影黑边
        Fill(new Rect(0f, 0f, Screen.width, 60f), new Color(0f, 0f, 0f, 0.72f));
        Fill(new Rect(0f, Screen.height - 44f, Screen.width, 44f), new Color(0f, 0f, 0f, 0.72f));

        float width = Mathf.Min(Screen.width - 150f, 1000f);
        Rect rect = new Rect((Screen.width - width) * 0.5f, Screen.height - 214f, width, 152f);
        DrawPanel(rect, new Color(0.04f, 0.05f, 0.07f, 0.94f), new Color(1f, 1f, 1f, 0.16f));

        // 说话人
        Fill(new Rect(rect.x + 16f, rect.y + 18f, 4f, 30f), btnBlue);
        GUI.Label(new Rect(rect.x + 32f, rect.y + 16f, width - 64f, 32f), line.speaker, speakerStyle);
        Fill(new Rect(rect.x + 32f, rect.y + 52f, width - 64f, 1f), dividerColor);

        // 正文（逐字）
        GUI.Label(new Rect(rect.x + 32f, rect.y + 62f, width - 64f, 60f), shown, dialogueStyle);

        // 右下角提示
        string hint = complete ? "按 E 继续  ▼" : "按 E 跳过";
        GUI.Label(new Rect(rect.x + width - 170f, rect.y + rect.height - 30f, 150f, 20f), hint, smallStyle);
    }

    // ── 点击开始（WebGL 需用户手势才能锁定鼠标）────────────
    private void DrawStartOverlay()
    {
        if (cursorLocked || dialogueIndex >= 0)
        {
            return;
        }

        float width = Mathf.Min(Screen.width - 120f, 460f);
        Rect rect = new Rect((Screen.width - width) * 0.5f, Screen.height * 0.5f - 40f, width, 80f);
        DrawPanel(rect, new Color(0.04f, 0.06f, 0.08f, 0.92f), panelBorder);
        GUI.Label(new Rect(rect.x + 20f, rect.y + 16f, rect.width - 40f, 26f), "点击画面开始", titleStyle);
        GUI.Label(new Rect(rect.x + 20f, rect.y + 44f, rect.width - 40f, 22f), "锁定鼠标后可转动视角　·　按住 Tab 可唤出鼠标", smallStyle);
    }

    // 昼夜转场：黑屏 + 居中文字
    private void DrawTransition()
    {
        if (fadeAlpha <= 0f)
        {
            return;
        }
        Fill(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, fadeAlpha));
        if (fadeAlpha > 0.25f)
        {
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01((fadeAlpha - 0.25f) / 0.5f));
            GUI.Label(new Rect(0f, Screen.height * 0.5f - 24f, Screen.width, 48f), fadeMessage, transitionStyle);
            GUI.color = Color.white;
        }
    }

    private void DrawStartError()
    {
        if (string.IsNullOrEmpty(startError))
        {
            return;
        }
        Rect rect = new Rect(16f, Screen.height - 96f, Screen.width - 32f, 60f);
        Fill(rect, new Color(0.45f, 0.08f, 0.08f, 0.95f));
        GUI.Label(new Rect(rect.x + 16f, rect.y + 8f, rect.width - 32f, 44f), "⚠ 初始化异常（请截图反馈）：" + startError, toastStyle);
    }

    private void DrawBudgetPanel()
    {
        Rect rect = BudgetRect;
        DrawPanel(rect, panelFill, panelBorder);
        Fill(new Rect(rect.x + 12f, rect.y + 14f, 4f, 44f), pendingColor);
        GUI.Label(new Rect(rect.x + 26f, rect.y + 16f, 280f, 28f), ProjectName, titleStyle);
        GUI.Label(new Rect(rect.x + 27f, rect.y + 46f, 300f, 18f), ProjectSubtitle + "　·　" + RankTitle(), smallStyle);
        GUI.Label(new Rect(rect.x + 27f, rect.y + 64f, 300f, 18f), ClockText, smallStyle);
        Fill(new Rect(rect.x + 22f, rect.y + 86f, rect.width - 44f, 1f), dividerColor);
        GUI.Label(new Rect(rect.x + 22f, rect.y + 94f, 288f, 20f), "收入 ¥" + income.ToString("N0") + " · 成本 ¥" + expenses.ToString("N0"), smallStyle);
        GUI.Label(new Rect(rect.x + 22f, rect.y + 116f, 288f, 20f), "财富值 ¥" + Cash.ToString("N0") + " · 净利 ¥" + (income - expenses).ToString("N0"), smallStyle);
    }

    private void DrawTaskList()
    {
        Rect rect = TaskListRect;
        DrawPanel(rect, panelFill, panelBorder);
        Fill(new Rect(rect.x + 12f, rect.y + 16f, 4f, 28f), fixedColor);
        GUI.Label(new Rect(rect.x + 26f, rect.y + 14f, 180f, 26f), "维修工单", titleStyle);
        GUI.Label(new Rect(rect.x + 27f, rect.y + 38f, 215f, 18f),
            "楼栋 " + UnlockedBuildingCount() + "/" + ResidenceCount + " · " + CountActive() + "进行中 · " + CountFixed() + "完工", smallStyle);

        // 折叠 / 展开
        Rect toggle = new Rect(rect.x + rect.width - 86f, rect.y + 16f, 70f, 28f);
        bool hoverToggle = toggle.Contains(Event.current.mousePosition);
        DrawPanel(toggle, hoverToggle ? Color.Lerp(btnBlue, Color.white, 0.15f) : btnBlue, Color.clear);
        if (GUI.Button(toggle, GUIContent.none, GUIStyle.none))
        {
            taskListExpanded = !taskListExpanded;
        }
        GUI.Label(toggle, taskListExpanded ? "收起" : "展开", cardButtonStyle);

        float y = rect.y + 64f;
        Fill(new Rect(rect.x + 18f, y, rect.width - 36f, 1f), dividerColor);
        y += 10f;

        List<Order> display = BuildDisplayList();
        if (display.Count == 0)
        {
            GUI.Label(new Rect(rect.x + 18f, y + 6f, rect.width - 36f, 22f), "暂无工单，系统正在派单…", smallStyle);
            return;
        }

        if (!taskListExpanded)
        {
            // 任务栏：只显示当前进行中的 1-2 条
            int show = Mathf.Min(2, display.Count);
            for (int i = 0; i < show; i++)
            {
                DrawOrderCard(new Rect(rect.x + 12f, y + i * 66f, rect.width - 24f, 58f), display[i]);
            }
            if (display.Count > show)
            {
                GUI.Label(new Rect(rect.x + 18f, y + show * 66f + 4f, rect.width - 36f, 18f),
                    "还有 " + (display.Count - show) + " 条工单 · 点「展开」查看", smallStyle);
            }
            return;
        }

        // 展开：固定高度内上下滚动（参考原神式滚动列表）
        float viewH = rect.height - 74f;
        float contentH = Mathf.Max(viewH, display.Count * 66f + 8f);
        taskScroll = GUI.BeginScrollView(new Rect(rect.x, y, rect.width, viewH), taskScroll,
            new Rect(0f, 0f, rect.width - 22f, contentH));
        for (int i = 0; i < display.Count; i++)
        {
            DrawOrderCard(new Rect(0f, i * 66f, rect.width - 24f, 58f), display[i]);
        }
        GUI.EndScrollView();
    }

    private void DrawOrderCard(Rect card, Order order)
    {
        bool isActive = order == activeOrder || order == repairingOrder;
        DrawPanel(card, isActive ? new Color(1f, 1f, 1f, 0.11f) : new Color(1f, 1f, 1f, 0.04f), Color.clear);
        Fill(new Rect(card.x + 9f, card.y + 8f, 4f, card.height - 16f), stateColors[(int)order.state]);

        GUI.Label(new Rect(card.x + 22f, card.y + 4f, card.width - 98f, 20f),
            order.Code + "  " + order.room + " · " + order.title, cardTitleStyle);

        // 归属角标：居民自修（绿）/ 须物业（橙）
        bool selfRep = OrderSelfRepairable(order);
        Rect badge = new Rect(card.x + card.width - 72f, card.y + 4f, 60f, 18f);
        Color ownColor = selfRep ? fixedColor : workingColor;
        Fill(badge, new Color(ownColor.r, ownColor.g, ownColor.b, 0.30f));
        GUI.Label(badge, selfRep ? "居民自修" : "须物业", cardButtonStyle);

        Color previous = GUI.color;
        GUI.color = StateTextColor(order);
        GUI.Label(new Rect(card.x + 22f, card.y + 23f, card.width - 32f, 18f),
            StateText(order) + "　预约 " + order.ScheduleText + "　¥" + order.cost.ToString("N0"), smallStyle);

        // 所需工具：没这件工具时标红，提醒去商店买
        bool hasTool = HasTool(order.requiredTool);
        Color toolColor = order.state == OrderState.Fixed ? fixedColor
            : (hasTool ? new Color(0.62f, 0.86f, 0.72f) : new Color(1f, 0.55f, 0.42f));
        Color prevTool = GUI.color;
        GUI.color = toolColor;
        GUI.Label(new Rect(card.x + 22f, card.y + 40f, card.width - 32f, 18f),
            (hasTool ? "工具：" : "缺工具：") + ToolName(order.requiredTool), smallStyle);
        GUI.color = prevTool;
        GUI.color = previous;
    }

    private string StateText(Order order)
    {
        if (order.state == OrderState.Repairing)
        {
            return "维修中 " + Mathf.RoundToInt(order.repairProgress * 100f) + "%";
        }
        if (order.state != OrderState.Fixed)
        {
            return "待维修";
        }
        return order.verified ? "已闭环" : "观察期";
    }

    private Color StateTextColor(Order order)
    {
        if (order.state == OrderState.Repairing)
        {
            return workingColor;
        }
        return order.state == OrderState.Fixed ? fixedColor : pendingColor;
    }

    // ── 工具背包 ──────────────────────────────────────────
    private void DrawToolChip()
    {
        if (tools.Count == 0)
        {
            return;
        }

        Rect rect = ToolChipRect;
        DrawPanel(rect, panelFill, panelBorder);
        Fill(new Rect(rect.x + 10f, rect.y + 9f, 4f, 18f), tools[currentTool].color);
        GUI.Label(new Rect(rect.x + 22f, rect.y + 8f, rect.width - 32f, 22f),
            "当前工具：" + tools[currentTool].name + "   [Q / 滚轮 切换]", smallStyle);
    }

    private void DrawBag()
    {
        if (!bagOpen || tools.Count == 0)
        {
            return;
        }

        Rect rect = BagRect;
        DrawPanel(rect, panelFill, panelBorder);
        Fill(new Rect(rect.x + 12f, rect.y + 14f, 4f, 28f), btnBlue);
        GUI.Label(new Rect(rect.x + 26f, rect.y + 14f, 200f, 26f), "工具包", titleStyle);
        GUI.Label(new Rect(rect.x + 27f, rect.y + 38f, 250f, 18f), "已拥有 " + UnlockedToolCount() + " 件　·　Q/滚轮 切换　·　B 收起", smallStyle);

        for (int i = 0; i < tools.Count; i++)
        {
            if (!tools[i].unlocked)
            {
                continue;
            }
            Rect row = new Rect(rect.x + 10f, rect.y + 62f + i * 32f, rect.width - 20f, 28f);
            bool isCurrent = i == currentTool;
            bool hover = row.Contains(Event.current.mousePosition);

            DrawPanel(row, isCurrent ? new Color(1f, 1f, 1f, 0.14f) : (hover ? new Color(1f, 1f, 1f, 0.08f) : new Color(1f, 1f, 1f, 0.03f)), Color.clear);
            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                currentTool = i;
                BuildToolModel();
            }

            Fill(new Rect(row.x + 10f, row.y + 9f, 10f, 10f), tools[i].color);
            GUI.Label(new Rect(row.x + 28f, row.y + 4f, row.width - 38f, 20f), tools[i].name, cardTitleStyle);
            if (isCurrent)
            {
                GUI.Label(new Rect(row.x + row.width - 60f, row.y + 6f, 52f, 18f), "使用中", smallStyle);
            }
        }
    }

    // ── 道具商店（G 键，可展开/收起）──────────────────────
    // ── 商店（G 召唤；←/→ 切分类，↑/↓ 选择，Enter 购买；也可鼠标点击）──
    private void DrawShop()
    {
        if (tools.Count == 0)
        {
            return;
        }

        if (!shopOpen)
        {
            return;   // 收起后完全不出现，不占屏幕
        }

        Rect rect = ShopRect;
        DrawPanel(rect, panelFill, panelBorder);
        Fill(new Rect(rect.x + 12f, rect.y + 16f, 4f, 28f), fixedColor);
        GUI.Label(new Rect(rect.x + 26f, rect.y + 14f, 200f, 26f), "商店", titleStyle);
        GUI.Label(new Rect(rect.x + 27f, rect.y + 38f, 240f, 18f), "现金 ¥" + Cash.ToString("N0"), smallStyle);

        Rect toggle = new Rect(rect.x + rect.width - 74f, rect.y + 16f, 62f, 26f);
        bool hoverToggle = toggle.Contains(Event.current.mousePosition);
        DrawPanel(toggle, hoverToggle ? Color.Lerp(btnBlue, Color.white, 0.15f) : btnBlue, Color.clear);
        if (GUI.Button(toggle, GUIContent.none, GUIStyle.none))
        {
            shopOpen = false;
        }
        GUI.Label(toggle, "收起", cardButtonStyle);

        string[] tabs = { "工具店", "服装店" };
        int itemCount = shopTab == 0 ? tools.Count : outfits.Count;
        Rect tabBar = new Rect(rect.x + 12f, rect.y + 60f, rect.width - 24f, 30f);
        float tabWidth = (tabBar.width - 8f) * 0.5f;
        for (int i = 0; i < tabs.Length; i++)
        {
            Rect tab = new Rect(tabBar.x + i * (tabWidth + 8f), tabBar.y, tabWidth, 30f);
            Fill(tab, i == shopTab ? btnBlue : new Color(1f, 1f, 1f, 0.06f));
            if (GUI.Button(tab, GUIContent.none, GUIStyle.none))
            {
                shopTab = i;
                shopCursor = 0;
            }
            GUI.Label(tab, tabs[i], cardButtonStyle);
        }
        GUI.Label(new Rect(rect.x + 12f, rect.y + rect.height - 22f, rect.width - 24f, 18f), "←/→ 切分类　↑/↓ 选择　Enter 购买", smallStyle);

        for (int i = 0; i < itemCount; i++)
        {
            Rect row = new Rect(rect.x + 10f, rect.y + 98f + i * 32f, rect.width - 20f, 28f);
            bool hover = row.Contains(Event.current.mousePosition);
            DrawPanel(row, i == shopCursor ? new Color(1f, 1f, 1f, 0.13f) : (hover ? new Color(1f, 1f, 1f, 0.08f) : new Color(1f, 1f, 1f, 0.03f)), Color.clear);

            if (shopTab == 0)
            {
                ToolInfo tool = tools[i];
                Fill(new Rect(row.x + 10f, row.y + 9f, 10f, 10f), tool.color);
                GUI.Label(new Rect(row.x + 28f, row.y + 4f, row.width - 130f, 20f), tool.name, cardTitleStyle);
                if (tool.unlocked)
                {
                    GUI.Label(new Rect(row.x + row.width - 76f, row.y + 6f, 66f, 18f), "已拥有", smallStyle);
                    continue;
                }
                GUI.Label(new Rect(row.x + row.width - 152f, row.y + 6f, 68f, 18f), "¥" + tool.price.ToString("N0"), smallStyle);
                bool afford = Cash >= tool.price;
                Rect buy = new Rect(row.x + row.width - 80f, row.y + 3f, 70f, 22f);
                DrawPanel(buy, afford ? btnBlue : new Color(0.28f, 0.3f, 0.33f, 0.9f), Color.clear);
                if (afford && GUI.Button(buy, GUIContent.none, GUIStyle.none))
                {
                    BuyTool(i);
                }
                GUI.Label(buy, "购买", cardButtonStyle);
            }
            else
            {
                Outfit outfit = outfits[i];
                Fill(new Rect(row.x + 10f, row.y + 9f, 10f, 10f), outfit.coat);
                GUI.Label(new Rect(row.x + 28f, row.y + 4f, row.width - 130f, 20f), outfit.name, cardTitleStyle);
                if (outfit.owned)
                {
                    bool wearing = i == currentOutfit;
                    GUI.Label(new Rect(row.x + row.width - 76f, row.y + 6f, 66f, 18f), wearing ? "穿着中" : "已拥有", smallStyle);
                    if (!wearing)
                    {
                        Rect wear = new Rect(row.x + row.width - 156f, row.y + 3f, 70f, 22f);
                        DrawPanel(wear, btnBlue, Color.clear);
                        if (GUI.Button(wear, GUIContent.none, GUIStyle.none))
                        {
                            ApplyOutfit(i);
                        }
                        GUI.Label(wear, "换上", cardButtonStyle);
                    }
                    continue;
                }
                GUI.Label(new Rect(row.x + row.width - 152f, row.y + 6f, 68f, 18f), "¥" + outfit.price.ToString("N0"), smallStyle);
                bool affordOutfit = Cash >= outfit.price;
                Rect buyOutfit = new Rect(row.x + row.width - 80f, row.y + 3f, 70f, 22f);
                DrawPanel(buyOutfit, affordOutfit ? btnBlue : new Color(0.28f, 0.3f, 0.33f, 0.9f), Color.clear);
                if (affordOutfit && GUI.Button(buyOutfit, GUIContent.none, GUIStyle.none))
                {
                    BuyOutfit(i);
                }
                GUI.Label(buyOutfit, "购买", cardButtonStyle);
            }
        }
    }

    // 商店键盘操作放在 Update 里：OnGUI 每帧会执行多次，从这里读输入会被重复触发
    private void HandleShopInput()
    {
        if (!shopOpen)
        {
            return;
        }

        int itemCount = shopTab == 0 ? tools.Count : outfits.Count;

        if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow))
        {
            shopTab = 1 - shopTab;
            shopCursor = 0;
            itemCount = shopTab == 0 ? tools.Count : outfits.Count;
            ShowToast(shopTab == 0 ? "工具店" : "服装店", 1.5f);
        }

        if (itemCount > 0)
        {
            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                shopCursor = (shopCursor - 1 + itemCount) % itemCount;
            }
            if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                shopCursor = (shopCursor + 1) % itemCount;
            }
            shopCursor = Mathf.Clamp(shopCursor, 0, itemCount - 1);
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            ActivateShopItem(shopCursor);
        }
    }

    private void ActivateShopItem(int index)
    {
        if (shopTab == 0)
        {
            BuyTool(index);
        }
        else
        {
            BuyOutfit(index);
        }
    }

    // ── 日历 / 任务单 / 账目本（N 键）────────────────────
    private Rect AlmanacRect { get { return new Rect(Screen.width * 0.5f - 300f, 96f, 600f, 380f); } }

    private void DrawAlmanac()
    {
        if (!almanacOpen)
        {
            return;
        }

        Rect rect = AlmanacRect;
        DrawPanel(rect, panelFill, panelBorder);
        Fill(new Rect(rect.x + 12f, rect.y + 16f, 4f, 28f), speakerStyle.normal.textColor);
        GUI.Label(new Rect(rect.x + 26f, rect.y + 14f, 300f, 26f), "日历 · 任务单 · 账目本", titleStyle);
        GUI.Label(new Rect(rect.x + 27f, rect.y + 38f, 300f, 18f), ClockText + "　·　N 收起", smallStyle);

        float half = (rect.width - 36f) * 0.5f;
        float top = rect.y + 66f;

        // 左：日历 + 预约任务单
        GUI.Label(new Rect(rect.x + 16f, top, half, 20f), "近期待办", cardTitleStyle);
        Fill(new Rect(rect.x + 16f, top + 22f, half, 1f), dividerColor);

        int shown = 0;
        int schedShown = 0;
        for (int day = DayIndex; day <= DayIndex + 2 && schedShown < 7; day++)
        {
            for (int i = 0; i < orders.Count && schedShown < 7; i++)
            {
                Order order = orders[i];
                if (order.state == OrderState.Fixed || order.schedDay != day)
                {
                    continue;
                }
                string tag = day == DayIndex ? "今天" : "第" + day + "天";
                Color previous = GUI.color;
                GUI.color = order == activeOrder || order == repairingOrder ? workingColor : new Color(0.88f, 0.92f, 0.95f);
                GUI.Label(new Rect(rect.x + 16f, top + 30f + schedShown * 20f, half, 18f),
                    tag + " " + order.schedHour.ToString("D2") + ":00  " + order.room, smallStyle);
                GUI.color = previous;
                GUI.Label(new Rect(rect.x + 16f + half * 0.58f, top + 30f + schedShown * 20f, half * 0.42f, 18f),
                    order.title, smallStyle);
                schedShown++;
            }
        }
        if (schedShown == 0)
        {
            GUI.Label(new Rect(rect.x + 16f, top + 30f, half, 18f), "暂无预约", smallStyle);
        }

        // 右：账目本
        float rx = rect.x + 16f + half + 20f;
        GUI.Label(new Rect(rx, top, half, 20f), "账目本", cardTitleStyle);
        Fill(new Rect(rx, top + 22f, half, 1f), dividerColor);
        GUI.Label(new Rect(rx, top + 28f, half, 18f), "日期　　　 收入　　成本", smallStyle);

        for (int i = ledger.Count - 1; i >= 0 && shown < 5; i--)
        {
            LedgerDay entry = ledger[i];
            string label = "第" + entry.day + "天";
            GUI.Label(new Rect(rx, top + 48f + shown * 18f, half, 18f),
                label + "　　¥" + entry.income.ToString("N0") + "　　¥" + entry.expense.ToString("N0"), smallStyle);
            shown++;
        }
        if (shown == 0)
        {
            GUI.Label(new Rect(rx, top + 48f, half, 18f), "今天还没有进出账", smallStyle);
        }

        Fill(new Rect(rx, top + 158f, half, 1f), dividerColor);
        GUI.Label(new Rect(rx, top + 166f, half, 20f), "累计收入　¥" + income.ToString("N0"), bodyStyle);
        GUI.Label(new Rect(rx, top + 188f, half, 20f), "累计成本　¥" + expenses.ToString("N0"), bodyStyle);
        Color prevColor = GUI.color;
        GUI.color = new Color(0.55f, 0.9f, 0.75f);
        GUI.Label(new Rect(rx, top + 212f, half, 22f), "净利润　　¥" + (income - expenses).ToString("N0"), hintStyle);
        GUI.color = prevColor;
        GUI.Label(new Rect(rx, top + 240f, half, 20f), "月薪 ¥" + MonthSalary.ToString("N0") + "（每 " + MonthDays + " 天发放）", smallStyle);
    }

    // ── 数字孪生监测平台（T 键）──────────────────────────
    private Rect TwinRect { get { return new Rect(Screen.width * 0.5f - 390f, 60f, 780f, 566f); } }
    // 收起时的紧凑状态条（顶部居中，不挡视野）
    private Rect TwinBarRect { get { return new Rect(Screen.width * 0.5f - 200f, 16f, 400f, 42f); } }

    // 现场详情：优先取离玩家最近的点位
    private Order FocusOrder()
    {
        Order best = null;
        float nearest = float.MaxValue;
        for (int i = 0; i < orders.Count; i++)
        {
            float d = Distance2D(playerPosition, orders[i].site);
            if (d < nearest)
            {
                nearest = d;
                best = orders[i];
            }
        }
        return best;
    }

    // ── 对比实验：蒙特卡洛模拟"数字孪生"vs"传统人工巡检" ──
    // 参数为设定值（用于演示，可在命题文档中补充行业出处）
    private string experimentText = "尚未运行";
    private float expTwinDetect, expTwinHours, expManualDetect, expManualHours, expTwinCost, expManualCost;

    private void RunComparisonExperiment()
    {
        const int trials = 200;
        const int hazardsPerTrial = 15;

        // 检出概率与响应时长（小时）—— 正态分布采样
        const float twinDetectP = 0.97f;
        const float manualDetectP = 0.68f;
        const float twinMean = 3.5f, twinSd = 1.2f;
        const float manualMean = 26f, manualSd = 8f;

        System.Random rng = new System.Random(20260918);

        float twinDetected = 0f, manualDetected = 0f;
        float twinHoursSum = 0f, manualHoursSum = 0f;
        float twinCostSum = 0f, manualCostSum = 0f;

        for (int t = 0; t < trials; t++)
        {
            for (int h = 0; h < hazardsPerTrial; h++)
            {
                if (rng.NextDouble() < twinDetectP)
                {
                    twinDetected += 1f;
                    twinHoursSum += SampleNormal(rng, twinMean, twinSd);
                }
                if (rng.NextDouble() < manualDetectP)
                {
                    manualDetected += 1f;
                    manualHoursSum += SampleNormal(rng, manualMean, manualSd);
                }
                twinCostSum += 620f;                 // 按清单核算的单点位均价
                manualCostSum += 620f * 1.28f;       // 传统方式含重复上门与返工
            }
        }

        float total = trials * hazardsPerTrial;
        expTwinDetect = twinDetected / total * 100f;
        expManualDetect = manualDetected / total * 100f;
        expTwinHours = twinDetected > 0f ? twinHoursSum / twinDetected : 0f;
        expManualHours = manualDetected > 0f ? manualHoursSum / manualDetected : 0f;
        expTwinCost = twinCostSum / trials;
        expManualCost = manualCostSum / trials;

        experimentText = "已完成 " + trials + " 组模拟实验（每组 " + hazardsPerTrial + " 个隐患点）";
        ShowToast("对比实验完成：数字孪生检出率 " + expTwinDetect.ToString("F1")
            + "%，传统人工 " + expManualDetect.ToString("F1") + "%", 6f);
    }

    private static float SampleNormal(System.Random rng, float mean, float sd)
    {
        // Box-Muller 变换
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        double normal = System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Sin(2.0 * System.Math.PI * u2);
        return Mathf.Max(0.2f, mean + (float)normal * sd);
    }

    private int AlarmCount()
    {
        int count = 0;
        for (int i = 0; i < orders.Count; i++)
        {
            if (orders[i].state != OrderState.Fixed && orders[i].sensorValue >= orders[i].sensorAlarm)
            {
                count++;
            }
        }
        return count;
    }

    // ── 小区俯视图：楼栋聚合、着色、点击选楼 ──────────────
    private void RebuildBuildingBounds()
    {
        buildingBounds.Clear();
        for (int i = 0; i < rooms.Count; i++)
        {
            Room r = rooms[i];
            float[] bb;
            if (!buildingBounds.TryGetValue(r.building, out bb))
            {
                bb = new float[] { r.xMin, r.xMax, r.zMin, r.zMax };
                buildingBounds[r.building] = bb;
            }
            else
            {
                if (r.xMin < bb[0]) bb[0] = r.xMin;
                if (r.xMax > bb[1]) bb[1] = r.xMax;
                if (r.zMin < bb[2]) bb[2] = r.zMin;
                if (r.zMax > bb[3]) bb[3] = r.zMax;
            }
        }
    }

    private Vector2 WorldToTwinMap(Vector3 world, Rect map)
    {
        float u = Mathf.InverseLerp(WorldMinX, WorldMaxX, world.x);
        float v = Mathf.InverseLerp(WorldMinZ, WorldMaxZ, world.z);
        return new Vector2(map.x + u * map.width, map.y + (1f - v) * map.height);
    }

    private string BuildingLabel(int b)
    {
        if (b == -1) return "员工宿舍";
        if (b == 0) return "装修公司";
        return b + "号楼";
    }

    private int BuildingOfOrder(Order o)
    {
        foreach (KeyValuePair<int, float[]> kv in buildingBounds)
        {
            float[] bb = kv.Value;
            if (o.site.x >= bb[0] - 0.5f && o.site.x <= bb[1] + 0.5f
                && o.site.z >= bb[2] - 0.5f && o.site.z <= bb[3] + 0.5f)
            {
                return kv.Key;
            }
        }
        return 0;
    }

    private Color BuildingColor(int b)
    {
        if (b > 0 && !IsBuildingUnlocked(b))
        {
            return new Color(1f, 1f, 1f, 0.10f);   // 未解锁：灰
        }
        bool alarm = false, active = false;
        for (int i = 0; i < orders.Count; i++)
        {
            Order o = orders[i];
            if (o.state == OrderState.Fixed || BuildingOfOrder(o) != b)
            {
                continue;
            }
            if (o.sensorValue >= o.sensorAlarm) alarm = true;
            else active = true;
        }
        if (alarm) return new Color(0.94f, 0.26f, 0.22f, 0.55f);
        if (active) return new Color(1f, 0.76f, 0.18f, 0.50f);
        return new Color(0.26f, 0.82f, 0.52f, 0.45f);
    }

    private bool TryHitBuilding(Vector2 mouse, Rect map, out int building)
    {
        building = 0;
        float u = (mouse.x - map.x) / map.width;
        float v = 1f - (mouse.y - map.y) / map.height;
        if (u < 0f || u > 1f || v < 0f || v > 1f)
        {
            return false;
        }
        float wx = Mathf.Lerp(WorldMinX, WorldMaxX, u);
        float wz = Mathf.Lerp(WorldMinZ, WorldMaxZ, v);
        foreach (KeyValuePair<int, float[]> kv in buildingBounds)
        {
            float[] bb = kv.Value;
            if (wx >= bb[0] && wx <= bb[1] && wz >= bb[2] && wz <= bb[3])
            {
                building = kv.Key;
                return true;
            }
        }
        return false;
    }

    private bool DrawTwinTab(Rect r, string label, bool active)
    {
        Fill(r, active ? btnBlue : new Color(1f, 1f, 1f, 0.06f));
        bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
        Color prev = GUI.color;
        GUI.color = active ? Color.white : new Color(0.72f, 0.79f, 0.78f);
        GUI.Label(r, label, cardButtonStyle);
        GUI.color = prev;
        return clicked;
    }

    // 俯视图画布：保持世界坐标比例（否则楼栋被拉长变形）
    private Rect TwinMapRect(Rect rect)
    {
        const float boxW = 420f;
        float top = rect.y + 70f;
        float aspect = (WorldMaxX - WorldMinX) / (WorldMaxZ - WorldMinZ);
        float w = boxW;
        float h = w / aspect;
        return new Rect(rect.x + 20f, top, w, h);
    }

    private void DrawTwinMap(Rect rect)
    {
        RebuildBuildingBounds();
        float top = rect.y + 70f;
        GUI.Label(new Rect(rect.x + 20f, top - 24f, rect.width - 40f, 18f),
            "小区俯视图　·　已解锁 " + UnlockedBuildingCount() + "/" + ResidenceCount + " 栋（每 " + OrdersPerBuilding + " 单解锁一栋）", cardTitleStyle);

        Rect map = TwinMapRect(rect);
        Fill(map, new Color(0f, 0f, 0f, 0.35f));

        // 楼栋色块
        foreach (KeyValuePair<int, float[]> kv in buildingBounds)
        {
            int b = kv.Key;
            float[] bb = kv.Value;
            Vector2 a = WorldToTwinMap(new Vector3(bb[0], 0f, bb[3]), map);
            Vector2 c = WorldToTwinMap(new Vector3(bb[1], 0f, bb[2]), map);
            Rect br = new Rect(Mathf.Min(a.x, c.x), Mathf.Min(a.y, c.y), Mathf.Abs(c.x - a.x), Mathf.Abs(c.y - a.y));
            Fill(br, BuildingColor(b));
            Fill(new Rect(br.x, br.y, br.width, 1f), new Color(1f, 1f, 1f, 0.25f));
            Fill(new Rect(br.x, br.yMax, br.width, 1f), new Color(1f, 1f, 1f, 0.25f));
            Fill(new Rect(br.x, br.y, 1f, br.height), new Color(1f, 1f, 1f, 0.25f));
            Fill(new Rect(br.xMax, br.y, 1f, br.height), new Color(1f, 1f, 1f, 0.25f));

            string label = BuildingLabel(b);
            if (b > 0 && !IsBuildingUnlocked(b))
            {
                label += "　未解锁";
            }
            GUI.Label(new Rect(br.x + 4f, br.y + 4f, br.width - 8f, 16f), label, smallStyle);
        }

        // 工单点位
        for (int i = 0; i < orders.Count; i++)
        {
            Vector2 p = WorldToTwinMap(orders[i].site, map);
            Fill(new Rect(p.x - 3f, p.y - 3f, 6f, 6f), stateColors[(int)orders[i].state]);
        }

        // 玩家
        Vector2 me = WorldToTwinMap(playerPosition, map);
        Fill(new Rect(me.x - 3.5f, me.y - 3.5f, 7f, 7f), Color.white);

        // 点击选楼
        if (Event.current.type == EventType.MouseDown && map.Contains(Event.current.mousePosition))
        {
            int hit;
            if (TryHitBuilding(Event.current.mousePosition, map, out hit))
            {
                if (hit > 0)
                {
                    if (IsBuildingUnlocked(hit))
                    {
                        twinSelectedBuilding = hit;
                    }
                    else
                    {
                        ShowToast(hit + "号楼尚未解锁（累计完成 " + ((hit - 1) * OrdersPerBuilding) + " 单后解锁）", 3.5f);
                    }
                }
            }
        }

        DrawTwinMapLegend(map);
        DrawBuildingRoster(map);
        DrawTwinMapSidebar(rect, top);
    }

    // 图例与进度：说明色块含义，并给出距下一栋解锁还差几单
    private void DrawTwinMapLegend(Rect map)
    {
        float y = map.yMax + 10f;
        float x = map.x;
        DrawLegendChip(x, y, pendingColor, "报警");
        DrawLegendChip(x + 74f, y, workingColor, "有工单");
        DrawLegendChip(x + 166f, y, fixedColor, "已消除");
        DrawLegendChip(x + 248f, y, new Color(1f, 1f, 1f, 0.18f), "未解锁");
        Fill(new Rect(x + 326f, y + 4f, 7f, 7f), Color.white);
        GUI.Label(new Rect(x + 338f, y, 80f, 16f), "你", smallStyle);

        int unlocked = UnlockedBuildingCount();
        string next = unlocked >= ResidenceCount
            ? "全部 " + ResidenceCount + " 栋已解锁"
            : "距解锁 " + (unlocked + 1) + "号楼还差 " + (OrdersPerBuilding - CountFixed() % OrdersPerBuilding) + " 单";
        GUI.Label(new Rect(map.x, y + 20f, map.width, 18f), next, smallStyle);
    }

    private void DrawLegendChip(float x, float y, Color color, string label)
    {
        Fill(new Rect(x, y + 4f, 8f, 8f), color);
        GUI.Label(new Rect(x + 12f, y, 64f, 16f), label, smallStyle);
    }

    // 楼栋名册：地图之外的第二入口，直接点名字也能查指标
    private void DrawBuildingRoster(Rect map)
    {
        float y = map.yMax + 48f;
        GUI.Label(new Rect(map.x, y, map.width, 18f), "楼栋名册（点击查看）", cardTitleStyle);
        y += 20f;

        for (int b = 1; b <= ResidenceCount; b++)
        {
            Rect row = new Rect(map.x, y, map.width, 20f);
            bool unlocked = IsBuildingUnlocked(b);
            int active = 0, alarm = 0, fixedCount = 0;
            for (int i = 0; i < orders.Count; i++)
            {
                Order o = orders[i];
                if (BuildingOfOrder(o) != b)
                {
                    continue;
                }
                if (o.state == OrderState.Fixed)
                {
                    fixedCount++;
                }
                else
                {
                    active++;
                    if (o.sensorValue >= o.sensorAlarm)
                    {
                        alarm++;
                    }
                }
            }

            bool selected = twinSelectedBuilding == b;
            if (selected || row.Contains(Event.current.mousePosition))
            {
                Fill(row, new Color(1f, 1f, 1f, selected ? 0.12f : 0.06f));
            }
            if (GUI.Button(row, GUIContent.none, GUIStyle.none))
            {
                if (unlocked)
                {
                    twinSelectedBuilding = b;
                }
                else
                {
                    ShowToast(b + "号楼尚未解锁（累计完成 " + ((b - 1) * OrdersPerBuilding) + " 单后解锁）", 3.5f);
                }
            }

            Fill(new Rect(row.x + 4f, row.y + 5f, 10f, 10f), BuildingColor(b));
            GUI.Label(new Rect(row.x + 22f, row.y + 1f, 90f, 18f), b + "号楼", smallStyle);
            GUI.Label(new Rect(row.x + 116f, row.y + 1f, row.width - 122f, 18f),
                unlocked
                    ? (alarm > 0 ? "报警 " + alarm : (active > 0 ? "监测中 " + active : "全部正常")) + "　·　已消除 " + fixedCount
                    : "未解锁（完成 " + ((b - 1) * OrdersPerBuilding) + " 单开放）",
                smallStyle);
            y += 22f;
        }
    }

    // 楼栋详情内容高度（与 DrawTwinMapSidebar 的排版增量保持一致）
    private float BuildingContentHeight(int b)
    {
        float h = 6f;
        for (int i = 0; i < rooms.Count; i++)
        {
            Room r = rooms[i];
            if (r.building != b)
            {
                continue;
            }
            h += 20f;
            bool any = false;
            for (int j = 0; j < orders.Count; j++)
            {
                if (orders[j].room != r.name)
                {
                    continue;
                }
                any = true;
                h += 36f;
            }
            if (!any)
            {
                h += 18f;
            }
        }
        return h;
    }

    private void DrawTwinMapSidebar(Rect rect, float top)
    {
        Rect side = new Rect(rect.x + 452f, top, rect.width - 472f, rect.height - 140f);
        DrawPanel(side, new Color(0f, 0f, 0f, 0.25f), dividerColor);

        int b = twinSelectedBuilding;
        if (b <= 0)
        {
            GUI.Label(new Rect(side.x + 14f, side.y + 12f, side.width - 28f, 20f), "点击左侧楼栋或名册，查看该楼各户实时指标", smallStyle);
            return;
        }

        GUI.Label(new Rect(side.x + 14f, side.y + 10f, side.width - 28f, 20f),
            BuildingLabel(b) + (IsBuildingUnlocked(b) ? "（已解锁）" : "（未解锁）"), cardTitleStyle);

        // 楼内工单会随经营不断累积，内容区滚动避免溢出面板
        float viewY = side.y + 36f;
        float viewH = side.height - 48f;
        float contentH = Mathf.Max(viewH, BuildingContentHeight(b));
        twinMapScroll = GUI.BeginScrollView(new Rect(side.x + 8f, viewY, side.width - 16f, viewH), twinMapScroll,
            new Rect(0f, 0f, side.width - 34f, contentH));

        float y = 4f;
        float cw = side.width - 34f;
        for (int i = 0; i < rooms.Count; i++)
        {
            Room r = rooms[i];
            if (r.building != b)
            {
                continue;
            }
            GUI.Label(new Rect(6f, y, cw - 12f, 18f), r.name, bodyStyle);
            y += 20f;
            bool any = false;
            for (int j = 0; j < orders.Count; j++)
            {
                Order o = orders[j];
                if (o.room != r.name)
                {
                    continue;
                }
                any = true;
                Color prev = GUI.color;
                GUI.color = StateTextColor(o);
                GUI.Label(new Rect(18f, y, cw - 24f, 18f), o.Code + " " + o.title + "　" + StateText(o), smallStyle);
                GUI.color = prev;
                y += 18f;
                GUI.Label(new Rect(18f, y, cw - 24f, 16f),
                    o.sensorName + " " + o.sensorValue.ToString("F1") + o.sensorUnit + "　·　"
                    + (OrderSelfRepairable(o) ? "居民自修" : "须物业"), smallStyle);
                y += 18f;
            }
            if (!any)
            {
                GUI.Label(new Rect(18f, y, cw - 24f, 18f), "无工单", smallStyle);
                y += 18f;
            }
        }

        GUI.EndScrollView();
    }

    private void DrawTwinPanel()
    {
        // 收起状态：只显示一条紧凑状态条，不遮挡视野
        if (!twinPanelOpen)
        {
            Rect bar = TwinBarRect;
            DrawPanel(bar, new Color(0.04f, 0.07f, 0.1f, 0.92f), new Color(0.45f, 0.75f, 0.9f, 0.3f));

            int alarms = AlarmCount();
            Fill(new Rect(bar.x + 12f, bar.y + 11f, 4f, 20f), alarms > 0 ? pendingColor : fixedColor);
            GUI.Label(new Rect(bar.x + 24f, bar.y + 10f, 250f, 22f),
                "数字孪生监测　·　报警 " + alarms + " 个　·　已消除 " + CountFixed() + "/" + orders.Count + "　·　已闭环 " + CountVerified(), smallStyle);

            Rect expand = new Rect(bar.x + bar.width - 84f, bar.y + 8f, 72f, 26f);
            bool hoverExpand = expand.Contains(Event.current.mousePosition);
            Fill(expand, hoverExpand ? Color.Lerp(btnBlue, Color.white, 0.18f) : btnBlue);
            if (GUI.Button(expand, GUIContent.none, GUIStyle.none))
            {
                twinPanelOpen = true;
            }
            GUI.Label(expand, "展开 T", cardButtonStyle);
            return;
        }

        Rect rect = TwinRect;
        // 展开动效：从略小尺寸淡入
        float ease = Mathf.Clamp01(panelFade);
        float inset = (1f - ease) * 14f;
        rect = new Rect(rect.x + inset, rect.y + inset * 0.6f, rect.width - inset * 2f, rect.height - inset * 1.2f);

        Color prevGui = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(0.35f + ease * 0.65f));
        DrawPanel(rect, new Color(0.04f, 0.07f, 0.1f, 0.96f), new Color(0.45f, 0.75f, 0.9f, 0.35f));

        GUI.Label(new Rect(rect.x + 20f, rect.y + 14f, 400f, 28f), "数字孪生监测平台　·　厨房改造工程", titleStyle);
        GUI.Label(new Rect(rect.x + 21f, rect.y + 42f, 560f, 18f), ClockText + "　·　监测点位 " + orders.Count + " 个　·　" + RankTitle() + "　·　楼栋 " + UnlockedBuildingCount() + "/" + ResidenceCount, smallStyle);

        Rect collapse = new Rect(rect.x + rect.width - 96f, rect.y + 16f, 76f, 30f);
        bool hoverCollapse = collapse.Contains(Event.current.mousePosition);
        Fill(collapse, hoverCollapse ? Color.Lerp(btnBlue, Color.white, 0.18f) : btnBlue);
        if (GUI.Button(collapse, GUIContent.none, GUIStyle.none))
        {
            twinPanelOpen = false;
        }
        GUI.Label(collapse, "收起 T", cardButtonStyle);

        // ── 页签：实时数据 / 小区俯视图 ──
        Rect tabLive = new Rect(rect.x + 428f, rect.y + 16f, 96f, 26f);
        Rect tabMap = new Rect(rect.x + 530f, rect.y + 16f, 96f, 26f);
        if (DrawTwinTab(tabLive, "实时数据", twinTab == 0)) { twinTab = 0; twinSelectedBuilding = 0; }
        if (DrawTwinTab(tabMap, "小区俯视图", twinTab == 1)) { twinTab = 1; }

        if (twinTab == 1)
        {
            DrawTwinMap(rect);
            return;
        }

        // ── 实时数据表 ──
        float top = rect.y + 70f;
        GUI.Label(new Rect(rect.x + 20f, top, 300f, 20f), "① 现场传感器实时数据", cardTitleStyle);
        Fill(new Rect(rect.x + 20f, top + 22f, rect.width - 40f, 1f), dividerColor);

        GUI.Label(new Rect(rect.x + 20f, top + 28f, 70f, 18f), "编号", smallStyle);
        GUI.Label(new Rect(rect.x + 92f, top + 28f, 170f, 18f), "点位 / 传感器", smallStyle);
        GUI.Label(new Rect(rect.x + 268f, top + 28f, 110f, 18f), "实时值", smallStyle);
        GUI.Label(new Rect(rect.x + 388f, top + 28f, 90f, 18f), "报警阈值", smallStyle);
        GUI.Label(new Rect(rect.x + 486f, top + 28f, 90f, 18f), "状态", smallStyle);
        GUI.Label(new Rect(rect.x + 566f, top + 28f, 100f, 18f), "验收", smallStyle);

        int shown = 0;
        for (int i = 0; i < orders.Count && shown < 6; i++)
        {
            Order order = orders[i];
            float ry = top + 50f + shown * 30f;
            if (ry > rect.y + 268f)
            {
                break;
            }

            bool fixedOrder = order.state == OrderState.Fixed;
            bool alarm = !fixedOrder && order.sensorValue >= order.sensorAlarm;
            Color stateColor = fixedOrder ? fixedColor : (alarm ? pendingColor : workingColor);
            string stateText = fixedOrder ? "已消除" : (alarm ? "报警" : "预警");

            GUI.Label(new Rect(rect.x + 20f, ry, 70f, 18f), order.Code, smallStyle);
            GUI.Label(new Rect(rect.x + 92f, ry, 170f, 18f), order.room + "　" + order.sensorName, smallStyle);

            Color prev = GUI.color;
            GUI.color = stateColor;
            GUI.Label(new Rect(rect.x + 268f, ry, 110f, 18f),
                order.sensorValue.ToString("F1") + " " + order.sensorUnit, smallStyle);
            GUI.color = prev;

            GUI.Label(new Rect(rect.x + 388f, ry, 90f, 18f), "> " + order.sensorAlarm.ToString("F1"), smallStyle);
            GUI.color = stateColor;
            GUI.Label(new Rect(rect.x + 486f, ry, 90f, 18f), stateText, smallStyle);
            GUI.Label(new Rect(rect.x + 566f, ry, 100f, 18f), order.verified ? "已闭环" : (fixedOrder ? "观察期" : "—"), smallStyle);
            GUI.color = prev;

            // 数值条
            float ratio = Mathf.Clamp01(order.sensorValue / Mathf.Max(0.001f, order.sensorMax));
            Fill(new Rect(rect.x + 20f, ry + 20f, rect.width - 40f, 4f), new Color(1f, 1f, 1f, 0.08f));
            Fill(new Rect(rect.x + 20f, ry + 20f, (rect.width - 40f) * ratio, 4f), stateColor);
            shown++;
        }
        if (shown == 0)
        {
            GUI.Label(new Rect(rect.x + 20f, top + 52f, 400f, 18f), "暂无监测点位", smallStyle);
        }

        // ── 现场点位详情：趋势曲线 + 阈值分区 + 规范 + 材料清单 ──
        float kpi = rect.y + 280f;
        GUI.Label(new Rect(rect.x + 20f, kpi, 400f, 20f), "② 现场点位详情（实时趋势 / 阈值区间）", cardTitleStyle);
        Fill(new Rect(rect.x + 20f, kpi + 22f, rect.width - 40f, 1f), dividerColor);

        Order focus = FocusOrder();
        if (focus != null)
        {
            float dy = kpi + 30f;
            GUI.Label(new Rect(rect.x + 20f, dy, 220f, 18f),
                focus.Code + "　" + focus.room + "　·　" + focus.sensorName, bodyStyle);

            // 归属：居民自修 / 须物业
            bool focusSelfRep = OrderSelfRepairable(focus);
            Color focusPrev = GUI.color;
            GUI.color = focusSelfRep ? fixedColor : workingColor;
            GUI.Label(new Rect(rect.x + 244f, dy, 72f, 18f), focusSelfRep ? "居民自修" : "须物业", smallStyle);
            GUI.color = focusPrev;

            // 趋势曲线
            float chartW = 300f;
            float chartH = 62f;
            Rect chart = new Rect(rect.x + 20f, dy + 24f, chartW, chartH);
            Fill(chart, new Color(0f, 0f, 0f, 0.45f));

            float maxRange = Mathf.Max(0.001f, focus.sensorMax);
            float alarmLine = chart.y + chartH * (1f - Mathf.Clamp01(focus.sensorAlarm / maxRange));
            Fill(new Rect(chart.x, alarmLine, chartW, 1f), new Color(0.95f, 0.35f, 0.3f, 0.85f));   // 报警阈值线
            GUI.Label(new Rect(chart.x + chartW + 6f, alarmLine - 9f, 120f, 18f), "报警阈值", smallStyle);

            int n = focus.history.Count;
            if (n > 1)
            {
                float step = chartW / (n - 1);
                Color lineColor = focus.state == OrderState.Fixed ? fixedColor
                    : (focus.sensorValue >= focus.sensorAlarm ? pendingColor : workingColor);
                for (int k = 0; k < n - 1; k++)
                {
                    float v0 = Mathf.Clamp01(focus.history[k] / maxRange);
                    float v1 = Mathf.Clamp01(focus.history[k + 1] / maxRange);
                    float y0 = chart.y + chartH * (1f - v0);
                    float y1 = chart.y + chartH * (1f - v1);
                    float h = Mathf.Max(1.5f, Mathf.Abs(y1 - y0));
                    Fill(new Rect(chart.x + k * step, Mathf.Min(y0, y1), Mathf.Max(1.5f, step), h), lineColor);
                }
            }
            GUI.Label(new Rect(chart.x, chart.y + chartH + 2f, chartW, 16f), "最近 12 秒趋势", smallStyle);

            // 阈值分区条
            float barY = dy + 128f;
            Rect zone = new Rect(rect.x + 20f, barY, chartW, 10f);
            float alarmRatio = Mathf.Clamp01(focus.sensorAlarm / maxRange);
            Fill(new Rect(zone.x, zone.y, zone.width * alarmRatio, zone.height), new Color(0.28f, 0.62f, 0.4f, 0.85f));
            Fill(new Rect(zone.x + zone.width * alarmRatio, zone.y, zone.width * (1f - alarmRatio), zone.height), new Color(0.72f, 0.28f, 0.26f, 0.85f));
            float valueRatio = Mathf.Clamp01(focus.sensorValue / maxRange);
            Fill(new Rect(zone.x + zone.width * valueRatio - 1f, zone.y - 3f, 3f, zone.height + 6f), Color.white);
            GUI.Label(new Rect(zone.x, barY + 14f, chartW, 16f),
                "正常区间 ← " + focus.sensorAlarm.ToString("F1") + " " + focus.sensorUnit + " → 报警区间　当前 "
                + focus.sensorValue.ToString("F1"), smallStyle);

            // 右栏：规范 / 材料 / 成因
            float rx = rect.x + 340f;
            GUI.Label(new Rect(rx, dy, 420f, 18f), "执行规范：" + StandardOf(focus), smallStyle);
            GUI.Label(new Rect(rx, dy + 22f, 420f, 18f), "材料清单：" + MaterialsOf(focus), smallStyle);
            GUI.Label(new Rect(rx, dy + 44f, 420f, 18f), "隐患成因：" + focus.cause, smallStyle);
            GUI.Label(new Rect(rx, dy + 66f, 420f, 18f), "处置方案：" + focus.plan, smallStyle);
            GUI.Label(new Rect(rx, dy + 92f, 420f, 18f),
                "验收判据：读数低于阈值 " + focus.sensorAlarm.ToString("F1") + focus.sensorUnit
                + " 并持续 " + ObserveHours.ToString("F0") + " 小时　→　"
                + (focus.verified ? "已闭环" : (focus.state == OrderState.Fixed ? "观察期中" : "未验收")), smallStyle);
            int matCost, labCost, manCost;
            CostBreakdown(focus.title, out matCost, out labCost, out manCost);
            GUI.Label(new Rect(rx, dy + 114f, 430f, 18f),
                "费用构成：材料 ¥" + matCost.ToString("N0") + "　人工 ¥" + labCost.ToString("N0")
                + "　管理 ¥" + manCost.ToString("N0") + "　合计 ¥" + focus.cost.ToString("N0"), smallStyle);
            GUI.Label(new Rect(rx, dy + 136f, 430f, 18f), "材料明细：" + PricedMaterials(focus.title), smallStyle);
        }

        // ── KPI 与对比 ──
        float ky = kpi + 190f;
        GUI.Label(new Rect(rect.x + 20f, ky, 300f, 20f), "③ 工程 KPI 与方案对比", cardTitleStyle);
        Fill(new Rect(rect.x + 20f, ky + 22f, rect.width - 40f, 1f), dividerColor);

        float lx = rect.x + 20f;
        float krx = rect.x + 400f;
        GUI.Label(new Rect(lx, ky + 30f, 200f, 18f), "本平台（数字孪生）", cardTitleStyle);
        GUI.Label(new Rect(krx, ky + 30f, 200f, 18f), "传统人工巡检", cardTitleStyle);

        // 有实验数据时优先展示模拟实验结果，否则展示本次运行实测
        bool hasExp = expTwinDetect > 0f;
        string[] labels = { "隐患检出率", "平均处置时长", "单点位成本", "闭环验收" };
        string[] digital =
        {
            hasExp ? expTwinDetect.ToString("F1") + " %" : HazardClearRate().ToString("F0") + " %",
            hasExp ? expTwinHours.ToString("F1") + " h" : ((AverageResponseHours() <= 0f) ? "—" : AverageResponseHours().ToString("F1") + " h"),
            hasExp ? "¥" + (expTwinCost / 15f).ToString("F0") : "¥620",
            hasExp ? "有" : "有",
        };
        string[] legacy =
        {
            hasExp ? expManualDetect.ToString("F1") + " %" : "68 %",
            hasExp ? expManualHours.ToString("F1") + " h" : LegacyResponseHours().ToString("F0") + " h",
            hasExp ? "¥" + (expManualCost / 15f).ToString("F0") : "¥794",
            "无",
        };

        for (int i = 0; i < labels.Length; i++)
        {
            float ry2 = ky + 54f + i * 22f;
            GUI.Label(new Rect(lx, ry2, 200f, 18f), labels[i], smallStyle);
            Color prev = GUI.color;
            GUI.color = new Color(0.6f, 0.92f, 0.75f);
            GUI.Label(new Rect(lx + 120f, ry2, 140f, 18f), digital[i], smallStyle);
            GUI.color = new Color(0.85f, 0.72f, 0.6f);
            GUI.Label(new Rect(krx + 120f, ry2, 140f, 18f), legacy[i], smallStyle);
            GUI.color = prev;
        }

        GUI.Label(new Rect(lx, ky + 54f + 4 * 22f, 740f, 18f), experimentText, smallStyle);

        // ── 累计经营 + 导出 ──
        float biz = ky + 158f;
        Fill(new Rect(rect.x + 20f, biz, rect.width - 40f, 1f), dividerColor);
        GUI.Label(new Rect(rect.x + 20f, biz + 10f, 520f, 18f),
            "累计改造投入 ¥" + expenses.ToString("N0")
            + "　　业主支付 ¥" + income.ToString("N0")
            + "　　净利 ¥" + (income - expenses).ToString("N0")
            + "　　已完成 " + CountFixed() + "/" + orders.Count, bodyStyle);

        Rect export = new Rect(rect.x + rect.width - 200f, biz + 4f, 180f, 34f);
        bool hoverExport = export.Contains(Event.current.mousePosition);
        Fill(export, hoverExport ? Color.Lerp(btnBlue, Color.white, 0.18f) : btnBlue);
        if (GUI.Button(export, GUIContent.none, GUIStyle.none))
        {
            ExportReport();
        }
        GUI.Label(export, "导出验收报告", cardButtonStyle);
    }

    // ── 验收报告：一键导出（体现"从问题输入到系统输出"的完整闭环）──
    private string BuildReportText()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine("==============================================");
        sb.AppendLine("  老旧住宅厨房改造工程 · 数字孪生运维验收报告");
        sb.AppendLine("==============================================");
        sb.AppendLine("生成时间：" + ClockText);
        sb.AppendLine("操作员　：" + (string.IsNullOrEmpty(currentAccount) ? "—" : currentAccount));
        sb.AppendLine();
        sb.AppendLine("一、项目概况");
        sb.AppendLine("  监测点位总数　　：" + orders.Count);
        sb.AppendLine("  已完成处置　　　：" + CountFixed());
        sb.AppendLine("  数据回归正常　　：" + CountSensorNormal() + "（改造后传感器读数回落至正常区间）");
        sb.AppendLine("  隐患消除率　　　：" + HazardClearRate().ToString("F1") + " %");
        sb.AppendLine("  平均处置时长　　：" + (AverageResponseHours() <= 0f ? "—" : AverageResponseHours().ToString("F1") + " 小时"));
        sb.AppendLine("  闭环验收率　　　：" + VerifyRate().ToString("F1") + " %（读数稳定低于阈值并持续 "
            + ObserveHours.ToString("F0") + " 游戏小时方判定闭环）");
        sb.AppendLine();
        sb.AppendLine("二、隐患清单与处置记录");
        for (int i = 0; i < orders.Count; i++)
        {
            Order o = orders[i];
            sb.AppendLine("  " + o.Code + "  " + o.room + "  " + o.title);
            sb.AppendLine("        传感器：" + o.sensorName + "  当前 " + o.sensorValue.ToString("F1") + o.sensorUnit
                + "  报警阈值 >" + o.sensorAlarm.ToString("F1") + o.sensorUnit);
            sb.AppendLine("        执行规范：" + StandardOf(o));
            sb.AppendLine("        材料清单：" + PricedMaterials(o.title));
            if (o.state == OrderState.Fixed)
            {
                int mc, lc, gc;
                CostBreakdown(o.title, out mc, out lc, out gc);
                sb.AppendLine("        处置状态：已消除　经费 ¥" + o.cost.ToString("N0")
                    + "（材料 ¥" + mc.ToString("N0") + " / 人工 " + lc.ToString("N0")
                    + " / 管理 " + gc.ToString("N0") + "）");
                sb.AppendLine("        验收：" + (o.verified ? "已闭环" : "观察期中"));
                sb.AppendLine("        处置方案：" + o.plan);
            }
            else
            {
                sb.AppendLine("        处置状态：未处置（持续监测中）");
            }
        }
        sb.AppendLine();
        sb.AppendLine("三、关键指标对比分析");
        sb.AppendLine("  指标              本平台(数字孪生)     传统人工巡检");
        sb.AppendLine("  隐患消除率        " + Pad(HazardClearRate().ToString("F0") + " %", 20) + Pad("68 %", 17));
        sb.AppendLine("  闭环验收率        " + Pad(VerifyRate().ToString("F0") + " %", 20) + Pad("无此环节", 17));
        sb.AppendLine("  平均处置时长      " + Pad((AverageResponseHours() <= 0f ? "—" : AverageResponseHours().ToString("F1") + " h"), 20) + Pad(LegacyResponseHours().ToString("F0") + " h", 17));
        sb.AppendLine("  漏检率            " + Pad("3 %", 20) + Pad(LegacyMissRate().ToString("F0") + " %", 17));
        sb.AppendLine("  改造成本          " + Pad("¥" + expenses.ToString("N0"), 20) + Pad("x" + LegacyCostFactor().ToString("F2"), 17));
        sb.AppendLine();
        if (expTwinDetect > 0f)
        {
            sb.AppendLine("  对比实验（蒙特卡洛模拟 200 组 × 15 个隐患点）：");
            sb.AppendLine("    指标            数字孪生        传统人工巡检");
            sb.AppendLine("    隐患检出率      " + Pad(expTwinDetect.ToString("F1") + " %", 16) + expManualDetect.ToString("F1") + " %");
            sb.AppendLine("    平均处置时长    " + Pad(expTwinHours.ToString("F1") + " h", 16) + expManualHours.ToString("F1") + " h");
            sb.AppendLine("    单点位成本      " + Pad("¥" + (expTwinCost / 15f).ToString("F0"), 16) + "¥" + (expManualCost / 15f).ToString("F0"));
            sb.AppendLine("    闭环验收环节    " + Pad("有", 16) + "无");
        }
        else
        {
            sb.AppendLine("  对比实验：尚未运行（可在监测平台点「运行对比实验」生成）");
        }
        sb.AppendLine();
        sb.AppendLine("  造价核算依据：材料费按市场参考单价计列并计 15% 损耗，");
        sb.AppendLine("  人工费按 " + LaborRate + " 元/工时计取，另计 15% 管理费。");
        sb.AppendLine();
        sb.AppendLine("四、经营数据");
        sb.AppendLine("  累计改造投入：" + "¥" + expenses.ToString("N0"));
        sb.AppendLine("  业主支付合计：" + "¥" + income.ToString("N0"));
        sb.AppendLine("  净利　　　　：" + "¥" + (income - expenses).ToString("N0"));
        sb.AppendLine();
        sb.AppendLine("五、结论");
        sb.AppendLine("  本平台以传感器实时数据驱动隐患排查与处置，隐患消除率、平均处置时长、");
        sb.AppendLine("  漏检率等关键指标均优于传统人工巡检方式，改造全过程数据可追溯。");
        sb.AppendLine("==============================================");
        return sb.ToString();
    }

    private static string Pad(string text, int width)
    {
        if (text == null)
        {
            text = string.Empty;
        }
        int displayWidth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            displayWidth += text[i] > 0x2E80 ? 2 : 1;
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder(text);
        while (displayWidth < width)
        {
            sb.Append(' ');
            displayWidth++;
        }
        return sb.ToString();
    }

    private void ExportReport()
    {
        string content = BuildReportText();
#if UNITY_WEBGL && !UNITY_EDITOR
        DownloadTextFile("厨房改造验收报告.txt", content);
        ShowToast("验收报告已导出到浏览器下载目录", 5f);
#else
        Debug.Log(content);
        ShowToast("报告已生成（编辑器模式输出到 Console）", 4f);
#endif
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")]
    private static extern void DownloadTextFile(string filename, string content);
#endif

    // 传感器读数已回到正常区间的点位数 —— 数字孪生"数据闭环"的直接证据
    private int CountVerified()
    {
        int count = 0;
        for (int i = 0; i < orders.Count; i++)
        {
            if (orders[i].verified)
            {
                count++;
            }
        }
        return count;
    }

    private float VerifyRate()
    {
        if (orders.Count == 0) return 0f;
        return (float)CountVerified() / orders.Count * 100f;
    }

    private int CountSensorNormal()
    {
        int count = 0;
        for (int i = 0; i < orders.Count; i++)
        {
            if (orders[i].fixTime > 0f)
            {
                count++;
            }
        }
        return count;
    }

    private void DrawPromptPanel()
    {
        Rect rect = PromptRect;

        if (repairingOrder != null)
        {
            DrawPanel(rect, panelFill, panelBorder);
            Fill(new Rect(rect.x + 12f, rect.y + 14f, 4f, rect.height - 28f), workingColor);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 12f, rect.width - 56f, 26f), repairingOrder.Code + " " + repairingOrder.room + " · " + repairingOrder.title, titleStyle);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 40f, rect.width - 56f, 20f), "方案：" + repairingOrder.plan, bodyStyle);
            Fill(new Rect(rect.x + 28f, rect.y + 68f, rect.width - 56f, 8f), new Color(1f, 1f, 1f, 0.12f));
            Fill(new Rect(rect.x + 28f, rect.y + 68f, (rect.width - 56f) * repairingOrder.repairProgress, 8f), workingColor);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 82f, rect.width - 56f, 22f),
                "维修中 " + Mathf.RoundToInt(repairingOrder.repairProgress * 100f) + "%　¥" + repairingOrder.cost.ToString("N0"), smallStyle);
            return;
        }

        if (activeOrder != null)
        {
            DrawPanel(rect, panelFill, panelBorder);
            Fill(new Rect(rect.x + 12f, rect.y + 14f, 4f, rect.height - 28f), pendingColor);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 12f, rect.width - 56f, 26f), activeOrder.Code + " " + activeOrder.room + " · " + activeOrder.title, titleStyle);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 40f, rect.width - 56f, 20f), "成因：" + activeOrder.cause, bodyStyle);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 62f, 240f, 20f), "核定经费 ¥" + activeOrder.cost.ToString("N0"), smallStyle);

            // 工具是否匹配
            bool toolOk = tools[currentTool].kind == activeOrder.requiredTool;
            bool ownsTool = HasTool(activeOrder.requiredTool);
            Color prevC = GUI.color;
            GUI.color = toolOk ? new Color(0.6f, 0.88f, 0.72f) : new Color(1f, 0.6f, 0.45f);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 84f, rect.width - 200f, 20f),
                (toolOk ? "工具就绪：" : "需用工具：") + ToolName(activeOrder.requiredTool)
                + (ownsTool ? "" : "（尚未拥有，去商店购买）"), smallStyle);
            GUI.color = prevC;

            Rect button = new Rect(rect.x + rect.width - 150f, rect.y + 66f, 132f, 34f);
            DrawPanel(button, toolOk ? btnBlue : new Color(0.32f, 0.3f, 0.3f, 0.95f), Color.clear);
            GUI.Label(button, toolOk ? "点击左键 维修" : "工具不对", cardButtonStyle);
            return;
        }

        if (activeColleague != null)
        {
            DrawPanel(rect, panelFill, panelBorder);
            Fill(new Rect(rect.x + 12f, rect.y + 14f, 4f, rect.height - 28f), btnBlue);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 14f, rect.width - 56f, 28f), activeColleague.name + "（同事）", titleStyle);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 46f, rect.width - 56f, 24f), "同事正在工位上，按 E 聊两句", bodyStyle);
            return;
        }

        DrawPanel(rect, new Color(0.04f, 0.06f, 0.08f, 0.6f), Color.clear);
        GUI.Label(new Rect(rect.x + 22f, rect.y + 14f, rect.width - 44f, 24f), "户主会到前台登记或电话预约", bodyStyle);
        GUI.Label(new Rect(rect.x + 22f, rect.y + 38f, rect.width - 44f, 20f),
            CountActive() < MaxActiveOrders ? "下一张工单约 " + Mathf.CeilToInt(orderTimer) + " 秒后到达" : "当前工单已满，先完成现场维修", smallStyle);
    }

    private void DrawHintBar()
    {
        Rect rect = HintRect;
        Fill(rect, new Color(0.03f, 0.05f, 0.07f, 0.9f));
        GUI.Label(rect, "WASD 移动　·　Shift 加速　·　空格 跳跃　·　左键 现场施工　·　E 对话　·　Q/滚轮 换工具　·　F 开关门　·　V 视角切换　·　P 静音　·　T 监测平台　·　K 知识手册　·　G 商店　·　B 工具包　·　N 日历账目　·　M 地图　·　L 联机　·　R 夜间休息　·　Tab 唤出鼠标", centerStyle);
    }

    private void DrawToast()
    {
        if (toastTimer <= 0f)
        {
            return;
        }
        float width = Mathf.Clamp(Screen.width - 760f, 260f, 560f);
        Rect rect = new Rect((Screen.width - width) * 0.5f, 18f, width, 46f);
        DrawPanel(rect, panelFill, panelBorder);
        Fill(new Rect(rect.x + 14f, rect.y + 8f, 4f, rect.height - 16f), toastTimer > 5f ? fixedColor : pendingColor);
        GUI.Label(new Rect(rect.x + 28f, rect.y, rect.width - 44f, rect.height), toastText, toastStyle);
    }

    // ── 绘制工具 ──────────────────────────────────────────
    private static void Fill(Rect rect, Color color)
    {
        Color previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }

    private const int RoundedRadius = 9;
    private static readonly Dictionary<Color, Texture2D> roundedTextures = new Dictionary<Color, Texture2D>();
    private static readonly Dictionary<Color, GUIStyle> roundedStyles = new Dictionary<Color, GUIStyle>();

    private static Texture2D RoundedTexture(Color color)
    {
        Texture2D texture;
        if (roundedTextures.TryGetValue(color, out texture))
        {
            return texture;
        }
        int size = RoundedRadius * 2 + 2;
        texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.hideFlags = HideFlags.HideAndDontSave;
        Color[] pixels = new Color[size * size];
        float limit = size - RoundedRadius;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float px = x + 0.5f;
                float py = y + 0.5f;
                float cx = Mathf.Clamp(px, RoundedRadius, limit);
                float cy = Mathf.Clamp(py, RoundedRadius, limit);
                float dx = px - cx;
                float dy = py - cy;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01(RoundedRadius - distance + 0.5f);
                pixels[y * size + x] = new Color(color.r, color.g, color.b, color.a * alpha);
            }
        }
        texture.SetPixels(pixels);
        texture.Apply();
        roundedTextures[color] = texture;
        return texture;
    }

    private static GUIStyle RoundedStyle(Color color)
    {
        GUIStyle style;
        if (roundedStyles.TryGetValue(color, out style))
        {
            return style;
        }
        style = new GUIStyle();
        style.normal.background = RoundedTexture(color);
        style.border = new RectOffset(RoundedRadius, RoundedRadius, RoundedRadius, RoundedRadius);
        style.padding = new RectOffset(0, 0, 0, 0);
        roundedStyles[color] = style;
        return style;
    }

    private static void DrawPanel(Rect rect, Color fill, Color border)
    {
        if (border.a > 0.001f)
        {
            GUI.Box(rect, GUIContent.none, RoundedStyle(border));
            rect = new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 2f);
        }
        GUI.Box(rect, GUIContent.none, RoundedStyle(fill));
    }

    private void EnsureStyles()
    {
        if (titleStyle != null)
        {
            return;
        }
        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        bodyStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = new Color(0.86f, 0.9f, 0.89f) } };
        smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(0.72f, 0.79f, 0.78f) } };
        buttonStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
        centerStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.72f, 0.79f, 0.82f) } };
        toastStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleLeft, wordWrap = true, normal = { textColor = new Color(0.88f, 0.92f, 0.91f) } };
        cardTitleStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        cardButtonStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
        speakerStyle = new GUIStyle(GUI.skin.label) { fontSize = 19, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.98f, 0.86f, 0.6f) } };
        dialogueStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, normal = { textColor = new Color(0.94f, 0.95f, 0.96f) } };
        transitionStyle = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.96f, 0.93f, 0.86f) } };

        Font font = UiFont;
        if (font != null)
        {
            titleStyle.font = font;
            hintStyle.font = font;
            bodyStyle.font = font;
            smallStyle.font = font;
            buttonStyle.font = font;
            centerStyle.font = font;
            toastStyle.font = font;
            cardTitleStyle.font = font;
            cardButtonStyle.font = font;
            speakerStyle.font = font;
            dialogueStyle.font = font;
            transitionStyle.font = font;
        }
    }

    private bool IsPointerOverGui(Vector2 mousePosition)
    {
        Vector2 point = new Vector2(mousePosition.x, Screen.height - mousePosition.y);
        return BudgetRect.Contains(point) || TaskListRect.Contains(point) || MinimapRect.Contains(point)
            || PromptRect.Contains(point) || ToolChipRect.Contains(point)
            || (bagOpen && BagRect.Contains(point)) || (shopOpen && ShopRect.Contains(point))
            || (almanacOpen && AlmanacRect.Contains(point))
            || (twinPanelOpen ? TwinRect.Contains(point) : TwinBarRect.Contains(point))
            || (lobbyOpen && LobbyRect.Contains(point)) || (inRoom && ChatRect.Contains(point))
            || (!loggedIn);
    }

    // ── 材质/几何工具 ─────────────────────────────────────
    private Material MakeMaterial(Color color, float metallic, float smoothness, bool emissive = false)
    {
        Shader shader = Shader.Find("Standard");
        if (shader == null)
        {
            shader = Shader.Find("Legacy Shaders/Diffuse");
        }
        if (shader == null)
        {
            shader = Shader.Find("Unlit/Color");
        }
        Material material = new Material(shader);
        material.color = color;
        if (material.HasProperty("_Metallic"))
        {
            material.SetFloat("_Metallic", metallic);
        }
        if (material.HasProperty("_Glossiness"))
        {
            material.SetFloat("_Glossiness", smoothness);
        }
        if (emissive && material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color * 0.4f);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }
        return material;
    }

    // 自发光材质：灯罩、城市窗户用它才能"自己亮"，而不是靠外部光照
    private Material MakeGlow(Color color, float intensity)
    {
        Material material = MakeMaterial(color, 0f, 0.6f);
        if (material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color * intensity);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }
        return material;
    }

    // 半透明材质（玻璃）：完整套用 Unity 官方的 Standard→Transparent 设置
    // 关键：SetOverrideTag("RenderType","Transparent") 不能漏，否则仍走不透明通道
    private Material MakeTransparent(Color color, float alpha, float smoothness)
    {
        // 优先用自带的极简透明着色器：混合状态写死在 shader 里，不依赖变体，绝不会退化成白色
        Shader shader = Shader.Find("Custom/Glass");
        if (shader == null)
        {
            shader = Shader.Find("Standard");
        }
        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
        }
        Material material = new Material(shader);

        // 同时把 Standard 路径需要的混合状态也设上（若回退到 Standard 依然透明）
        material.SetOverrideTag("RenderType", "Transparent");
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

        if (material.HasProperty("_Mode"))
        {
            material.SetFloat("_Mode", 3f);
        }
        if (material.HasProperty("_Metallic"))
        {
            material.SetFloat("_Metallic", 0f);
        }
        if (material.HasProperty("_Glossiness"))
        {
            material.SetFloat("_Glossiness", smoothness);
        }
        material.color = new Color(color.r, color.g, color.b, alpha);
        return material;
    }

    private static GameObject MakePrimitive(PrimitiveType type, string objectName, Transform parent, Vector3 localPosition, Vector3 localScale, Quaternion localRotation, Material material)
    {
        GameObject instance = GameObject.CreatePrimitive(type);
        instance.name = objectName;
        instance.transform.SetParent(parent, false);
        instance.transform.localPosition = localPosition;
        instance.transform.localRotation = localRotation;
        instance.transform.localScale = localScale;
        instance.GetComponent<Renderer>().sharedMaterial = material;
        return instance;
    }

    // 共享材质：同一颜色复用同一个 Material 实例，避免上千个物件各持一份材质导致无法批处理
    private static readonly Dictionary<Color, Material> simpleMaterials = new Dictionary<Color, Material>();

    // ── 程序化贴图（不依赖任何美术资源，运行时生成）──────────
    private static Texture2D woodTexture;
    private static Texture2D tileTexture;
    private static Texture2D brickTexture;
    private static Texture2D fabricTexture;
    private static Texture2D stoneTexture;

    private static void EnsureTextures()
    {
        if (woodTexture != null)
        {
            return;
        }
        woodTexture = MakeWoodTexture();
        tileTexture = MakeTileTexture();
        brickTexture = MakeBrickTexture();
        fabricTexture = MakeFabricTexture();
        stoneTexture = MakeStoneTexture();
    }

    private static Texture2D NewTexture(int size)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGB24, true);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;
        tex.hideFlags = HideFlags.HideAndDontSave;
        return tex;
    }

    // 木纹：横向年轮 + 细噪声
    private static Texture2D MakeWoodTexture()
    {
        const int size = 256;
        Texture2D tex = NewTexture(size);
        Color[] px = new Color[size * size];
        System.Random rng = new System.Random(7);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float grain = Mathf.Sin((y + Mathf.Sin(x * 0.05f) * 6f) * 0.45f) * 0.5f + 0.5f;
                grain = Mathf.Pow(grain, 0.6f);
                float noise = (float)rng.NextDouble() * 0.06f - 0.03f;
                float v = 0.72f + grain * 0.2f + noise;
                px[y * size + x] = new Color(v, v * 0.82f, v * 0.62f);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    // 瓷砖：网格缝
    private static Texture2D MakeTileTexture()
    {
        const int size = 256;
        const int cell = 64;
        Texture2D tex = NewTexture(size);
        Color[] px = new Color[size * size];
        System.Random rng = new System.Random(11);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool grout = (x % cell < 3) || (y % cell < 3);
                float noise = (float)rng.NextDouble() * 0.04f - 0.02f;
                float v = grout ? 0.55f : (0.93f + noise);
                px[y * size + x] = new Color(v, v, v);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    // 砖墙：错缝砖块
    private static Texture2D MakeBrickTexture()
    {
        const int size = 256;
        const int bh = 26;
        const int bw = 62;
        Texture2D tex = NewTexture(size);
        Color[] px = new Color[size * size];
        System.Random rng = new System.Random(23);
        for (int y = 0; y < size; y++)
        {
            int row = y / bh;
            int offset = (row % 2 == 0) ? 0 : bw / 2;
            for (int x = 0; x < size; x++)
            {
                int bx = (x + offset) % bw;
                bool mortar = (y % bh < 3) || (bx < 3);
                float tint = 0.86f + (float)rng.NextDouble() * 0.16f;
                float v = mortar ? 0.72f : (0.62f * tint);
                px[y * size + x] = new Color(v, v * 0.92f, v * 0.86f);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    // 织物：细密交错纹
    private static Texture2D MakeFabricTexture()
    {
        const int size = 128;
        Texture2D tex = NewTexture(size);
        Color[] px = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float weave = ((x % 4 < 2) == (y % 4 < 2)) ? 0.06f : -0.06f;
                float v = 0.92f + weave;
                px[y * size + x] = new Color(v, v, v);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    // 石材：斑驳颗粒
    private static Texture2D MakeStoneTexture()
    {
        const int size = 128;
        Texture2D tex = NewTexture(size);
        Color[] px = new Color[size * size];
        System.Random rng = new System.Random(31);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float n = (float)rng.NextDouble() * 0.14f - 0.07f;
                float v = 0.95f + n;
                px[y * size + x] = new Color(v, v, v * 0.98f);
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    private static readonly Dictionary<string, Material> texturedMaterials = new Dictionary<string, Material>();

    // 带贴图的共享材质（按 颜色+贴图+平铺 缓存，保证仍可合批）
    private static Material TexturedMaterial(Color color, Texture2D tex, float tiling)
    {
        string key = color.ToString() + "|" + tex.name + "|" + tiling.ToString("F1");
        Material material;
        if (texturedMaterials.TryGetValue(key, out material) && material != null)
        {
            return material;
        }
        material = new Material(Shader.Find("Standard"));
        material.color = color;
        material.mainTexture = tex;
        material.mainTextureScale = new Vector2(tiling, tiling);
        material.SetFloat("_Metallic", 0.02f);
        material.SetFloat("_Glossiness", 0.35f);
        texturedMaterials[key] = material;
        return material;
    }

    private static Material SimpleMaterial(Color color)
    {
        Material material;
        if (simpleMaterials.TryGetValue(color, out material) && material != null)
        {
            return material;
        }
        material = new Material(Shader.Find("Standard"));
        material.color = color;
        material.SetFloat("_Metallic", 0.02f);
        material.SetFloat("_Glossiness", 0.4f);
        simpleMaterials[color] = material;
        return material;
    }

    private GameObject CreateCube(string objectName, Vector3 position, Vector3 scale, Color color)
    {
        return CreateCube(objectName, position, scale, SimpleMaterial(color));
    }

    private GameObject CreateCube(string objectName, Vector3 position, Vector3 scale, Material material)
    {
        GameObject instance = GameObject.CreatePrimitive(PrimitiveType.Cube);
        instance.name = objectName;
        instance.transform.SetParent(transform);
        instance.transform.position = position;
        instance.transform.localScale = scale;
        instance.GetComponent<Renderer>().sharedMaterial = material;
        generatedObjects.Add(instance);
        return instance;
    }

    private GameObject CreateDecoCube(string objectName, Vector3 position, Vector3 scale, Color color)
    {
        return CreateDecoCube(objectName, position, scale, SimpleMaterial(color));
    }

    private GameObject CreateDecoCube(string objectName, Vector3 position, Vector3 scale, Material material)
    {
        GameObject instance = CreateCube(objectName, position, scale, material);
        Collider collider = instance.GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = false;
        }
        return instance;
    }

    private GameObject CreateDecoCube(string objectName, Vector3 position, Vector3 scale, Quaternion rotation, Material material)
    {
        GameObject instance = CreateCube(objectName, position, scale, material);
        instance.transform.localRotation = rotation;
        Collider collider = instance.GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = false;
        }
        return instance;
    }

    private GameObject CreateCylinder(string objectName, Vector3 position, float radius, float height, Quaternion rotation, Material material)
    {
        GameObject instance = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        instance.name = objectName;
        instance.transform.SetParent(transform);
        instance.transform.position = position;
        instance.transform.rotation = rotation;
        instance.transform.localScale = new Vector3(radius, height * 0.5f, radius);
        instance.GetComponent<Renderer>().sharedMaterial = material;
        generatedObjects.Add(instance);
        return instance;
    }
}
