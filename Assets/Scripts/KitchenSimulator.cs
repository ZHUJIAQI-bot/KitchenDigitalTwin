using System.Collections.Generic;
using UnityEngine;

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

    // ── 时间系统：1 游戏小时 = 2.5 秒真实时间；7 天为一个月 ──
    private const float RealSecondsPerGameHour = 7f;
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
        public float fixTime;          // 数据回归正常的时刻

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

        public OrderTemplate(string roomType, string title, string cause, string plan, int costMin, int costMax, float dx, float dz, ToolKind tool,
            string sensorName, string sensorUnit, float sensorNormal, float sensorAlarm, float sensorMax)
        {
            this.tool = tool;
            this.sensorName = sensorName;
            this.sensorUnit = sensorUnit;
            this.sensorNormal = sensorNormal;
            this.sensorAlarm = sensorAlarm;
            this.sensorMax = sensorMax;
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

    private readonly Color panelFill = new Color(0.055f, 0.075f, 0.095f, 0.97f);
    private readonly Color panelBorder = new Color(1f, 1f, 1f, 0.10f);
    private readonly Color dividerColor = new Color(1f, 1f, 1f, 0.08f);
    private readonly Color btnBlue = new Color(0.18f, 0.47f, 0.63f);

    // ── 运行时状态 ────────────────────────────────────────
    private readonly List<Order> orders = new List<Order>();
    private readonly List<OrderTemplate> templates = new List<OrderTemplate>();
    private readonly List<Room> rooms = new List<Room>();
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
    private const float WallHeight = 2.75f;
    private const float DoorHeight = 2.1f;

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

    // 门
    private class HouseDoor
    {
        public Transform pivot;
        public float open;        // 0 关 / 1 开
        public float target;
        public float xMin;
        public float xMax;
        public float z;
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
    private float voiceBurst;   // 当前这句还剩多久发声，到 0 就安静下来
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
    private float introDelay = 1.2f;
    private float typeTimer;
    private const float DialogueTypeTime = 1e6f;   // 单句最大显示时长（用于逐字进度）
    private const float TypeCharsPerSecond = 34f;  // 逐字显示速度
    private Homeowner talkTarget;                  // 对话结束后要登记工单的户主
    private CharacterRig bossRig;

    // 小地图
    private const float WorldMinX = -24f;
    private const float WorldMaxX = 66f;
    private const float WorldMinZ = -20f;
    private const float WorldMaxZ = 28f;

    private Order activeOrder;
    private Order repairingOrder;
    private int orderSerial;
    private float orderTimer;
    private bool taskListExpanded = true;
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
    private string loginMessage = "";
    private string currentAccount = "";
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
        QualitySettings.shadowDistance = 45f;
        QualitySettings.shadowCascades = 1;
        QualitySettings.pixelLightCount = 6;

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
            BuildTools();
            BuildAudio();
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

        HandleMovement();
        HandleDialogue();
        UpdateSensors();
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
        { -20f, -8f, -7f, 5f },     // 公司
        { 2f, 14f, -7f, 5f },       // 1号楼
        { 18f, 30f, -7f, 5f },      // 2号楼
        { 34f, 46f, -7f, 5f },      // 3号楼
        { 50f, 62f, -7f, 5f },      // 4号楼
        { 10f, 22f, 13f, 25f },     // 5号楼（第二排）
        { 26f, 38f, 13f, 25f },     // 6号楼（第二排）
        { 42f, 54f, 13f, 25f },     // 7号楼（第二排）
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
        private static string Hash(string input)
        {
            int h = 17;
            for (int i = 0; i < input.Length; i++)
            {
                h = h * 31 + input[i];
            }
            return h.ToString("X8");
        }

        public static void Save(string user, string password, int coat, int trouser)
        {
            string value = Hash(password) + "|" + coat + "|" + trouser;
            PlayerPrefs.SetString(Prefix + user.ToLowerInvariant(), value);
            PlayerPrefs.Save();
        }

        public static bool TryLoad(string user, string password, out int coat, out int trouser)
        {
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
            if (colleagues[i].root != null)
            {
                protectedRoots.Add(colleagues[i].root);
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
            if (objectName.StartsWith("Globe") || objectName.StartsWith("Label"))
            {
                continue;   // 灯罩要换材质、文字稍后要改材质，保持独立
            }

            bool isProtected = false;
            Transform t = filter.transform;
            while (t != null)
            {
                if (protectedRoots.Contains(t))
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
        rig.leftArm.localRotation = Quaternion.Euler(-62f, 0f, 0f);
        rig.rightArm.localRotation = Quaternion.Euler(-62f, 0f, 0f);
        colleagues.Add(new Colleague { name = name, root = rig.root, lines = lines });
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
        Color daySky = new Color(0.53f, 0.76f, 0.94f);
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
        RenderSettings.ambientLight = Color.Lerp(new Color(0.25f, 0.28f, 0.37f), new Color(0.62f, 0.63f, 0.64f), dayFactor);
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

    // 转场淡入淡出
    private void StartTransition(string message)
    {
        fadeAlpha = 1f;
        fadeMessage = message;
    }

    // 传感器实时数据仿真：报警值带噪声波动；改造完成后回落到正常值
    private void UpdateSensors()
    {
        float dt = Time.deltaTime;
        for (int i = 0; i < orders.Count; i++)
        {
            Order order = orders[i];
            bool fixedOrder = order.state == OrderState.Fixed;
            float target = fixedOrder ? order.sensorNormal : order.sensorAlarm * 1.25f;

            float noise = 1f
                + Mathf.Sin(Time.time * 2.1f + i * 1.7f) * 0.035f
                + Mathf.Sin(Time.time * 7.3f + i * 0.9f) * 0.012f;

            order.sensorValue = Mathf.Lerp(order.sensorValue, target, dt * (fixedOrder ? 1.6f : 0.7f)) * noise;

            // 记录"数据回归正常"的时刻，用于统计处置时长
            if (fixedOrder && order.fixTime <= 0f
                && Mathf.Abs(order.sensorValue - order.sensorNormal) < Mathf.Max(0.05f, order.sensorAlarm * 0.08f))
            {
                order.fixTime = gameTime;
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
            float z = -58f - Random.Range(0f, 26f);
            BuildCityTower(new Vector3(x, 0f, z), Random.Range(12f, 38f), Random.Range(8f, 15f),
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
        CreateWorldLabel("焕新维修公司", new Vector3(-16.4f, 2.35f, z0 - 0.34f), 0.08f, Color.white);   // 3.07 × 0.56

        CreateDecoCube("Price Board", new Vector3(-12.6f, 1.45f, z0 - 0.25f), new Vector3(3.4f, 2.2f, 0.1f), new Color(0.93f, 0.92f, 0.88f));
        CreateDecoCube("Price Board Frame", new Vector3(-12.6f, 1.45f, z0 - 0.19f), new Vector3(3.7f, 2.5f, 0.08f), new Color(0.35f, 0.28f, 0.2f));
        CreateWorldLabel("维 修 价 目 表\n────────\n水路渗漏 ¥3200\n电路检修 ¥2600\n燃气管道 ¥4600\n墙面翻新 ¥2800",
            new Vector3(-12.6f, 1.45f, z0 - 0.33f), 0.040f, new Color(0.15f, 0.15f, 0.18f));           // 2.05 × 1.24

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

        rooms.Add(new Room { name = "装修公司", type = "公司", center = new Vector3(-14f, 0f, -1f), xMin = x0, xMax = x1, zMin = z0, zMax = z1 });

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
            renderer.sharedMaterial = mesh.font.material;
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
        BuildResidence(4, 50f, -7f, roofColors[3]);
        // 第二排（错开半格，形成小区内街）
        BuildResidence(5, 10f, 13f, roofColors[4]);
        BuildResidence(6, 26f, 13f, roofColors[5]);
        BuildResidence(7, 42f, 13f, roofColors[6]);
    }

    // 单层住宅：12×12，四个 6×6 房间
    //   前左 客厅（入户）/ 前右 厨房
    //   后左 卫生间       / 后右 卧室
    private void BuildResidence(int index, float x0, float z0, Color roofColor)
    {
        float x1 = x0 + 6f, x2 = x0 + 12f;
        float zMid = z0 + 6f, z1 = z0 + 12f;
        Color wall = new Color(0.88f, 0.86f, 0.81f);
        Color inner = new Color(0.84f, 0.82f, 0.77f);
        string tag = index + "号楼";

        // 房间地板：整块铺装（用色区分房型，避免大量小方块拖慢 WebGL）
        BuildRoomFloor(x0, x1, z0, zMid, new Color(0.82f, 0.72f, 0.58f));  // 客厅 木地板
        BuildRoomFloor(x1, x2, z0, zMid, new Color(0.76f, 0.78f, 0.76f));  // 厨房 灰砖
        BuildRoomFloor(x0, x1, zMid, z1, new Color(0.8f, 0.85f, 0.86f));   // 卫生间 浅蓝砖
        BuildRoomFloor(x1, x2, zMid, z1, new Color(0.85f, 0.76f, 0.62f));  // 卧室 木地板

        // 外墙：前墙留门洞，其余墙开窗
        BuildWallWithOpenings("Res Front Wall", true, z0, x0, x2, 0.24f, wall,
            x0 + 2f, x0 + 4.2f, 0f, 2.2f);
        BuildWallWithOpenings("Res Back Wall", true, z1, x0, x2, 0.24f, wall,
            x0 + 1.5f, x0 + 4f, 0.95f, 2.15f,
            x0 + 8f, x0 + 10.5f, 0.95f, 2.15f);
        BuildWallWithOpenings("Res Left Wall", false, x0, z0, z1, 0.24f, wall,
            z0 + 2.5f, z0 + 5f, 0.95f, 2.15f,
            zMid + 1.5f, zMid + 4f, 0.95f, 2.15f);
        BuildWallWithOpenings("Res Right Wall", false, x2, z0, z1, 0.24f, wall,
            z0 + 2.5f, z0 + 5f, 0.95f, 2.15f);

        AddGlassPane(true, z1 - 0.14f, x0 + 1.5f, x0 + 4f, 0.95f, 2.15f);
        AddGlassPane(true, z1 - 0.14f, x0 + 8f, x0 + 10.5f, 0.95f, 2.15f);
        AddGlassPane(false, x0 + 0.14f, z0 + 2.5f, z0 + 5f, 0.95f, 2.15f);
        AddGlassPane(false, x0 + 0.14f, zMid + 1.5f, zMid + 4f, 0.95f, 2.15f);
        AddGlassPane(false, x2 - 0.14f, z0 + 2.5f, z0 + 5f, 0.95f, 2.15f);

        // 内墙：竖向 x=x1，横向 z=zMid
        BuildWallWithOpenings("Res Wall Vertical", false, x1, z0, z1, 0.22f, inner,
            z0 + 2f, z0 + 4f, 0f, DoorHeight,
            zMid + 2f, zMid + 4f, 0f, DoorHeight);
        BuildWallWithOpenings("Res Wall Horizontal", true, zMid, x0, x2, 0.22f, inner,
            x0 + 1.5f, x0 + 3.5f, 0f, DoorHeight,
            x0 + 8.5f, x0 + 10.5f, 0f, DoorHeight);

        BuildRoof(tag + " Roof", x0 + 6f, zMid - 1f, 12f, 12f, roofColor);

        // 房间（同一栋楼共用同一个入户门位置）
        Vector3 doorPoint = new Vector3(x0 + 3.1f, GroundLevel, z0 + 1.6f);
        AddRoom(tag + " 客厅", "客厅", x0, x1, z0, zMid, doorPoint);
        AddRoom(tag + " 厨房", "厨房", x1, x2, z0, zMid, doorPoint);
        AddRoom(tag + " 卫生间", "卫生间", x0, x1, zMid, z1, doorPoint);
        AddRoom(tag + " 卧室", "卧室", x1, x2, zMid, z1, doorPoint);

        BuildLivingRoom(x0 + 3f, z0 + 3f);
        BuildKitchenRoom(x1 + 3f, z0 + 3f);
        BuildBathroom(x0 + 3f, zMid + 3f);
        BuildBedroom(x1 + 3f, zMid + 3f);

        BuildCeilingLight(x0 + 3f, z0 + 3f);
        BuildCeilingLight(x1 + 3f, z0 + 3f);
        BuildCeilingLight(x0 + 3f, zMid + 3f);
        BuildCeilingLight(x1 + 3f, zMid + 3f);
        AddRoomLight(x0 + 6f, zMid - 1f, 17f);

        BuildHouseDoor(tag + " Door", x0 + 2f, x0 + 4.2f, z0);

        // 门牌号（门右侧墙面外侧）
        CreateDecoCube(tag + " Plate", new Vector3(x0 + 5.4f, 1.75f, z0 - 0.19f), new Vector3(1.5f, 0.6f, 0.08f), new Color(0.16f, 0.24f, 0.4f));
        CreateWorldLabel(index + "号楼", new Vector3(x0 + 5.4f, 1.75f, z0 - 0.26f), 0.05f, Color.white);   // 0.96 × 0.35
    }

    private void AddRoom(string name, string type, float xMin, float xMax, float zMin, float zMax, Vector3 doorPoint)
    {
        rooms.Add(new Room
        {
            name = name,
            type = type,
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
                nearest.target = nearest.target > 0.5f ? 0f : 1f;
                ShowToast(nearest.target > 0.5f ? "开门" : "关门", 1.5f);
            }
        }

        for (int i = 0; i < houseDoors.Count; i++)
        {
            HouseDoor door = houseDoors[i];
            if (door.pivot == null)
            {
                continue;
            }
            door.open = Mathf.Lerp(door.open, door.target, Time.deltaTime * 5f);
            door.pivot.localRotation = Quaternion.Euler(0f, -95f * door.open, 0f);
        }

        // 公司感应玻璃门：靠近自动滑开
        if (sensorDoorLeft != null && sensorDoorRight != null)
        {
            float distance = Distance2D(playerPosition, sensorDoorCenter);
            float target = distance < 3.2f ? 1f : 0f;
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
        Material top = MakeMaterial(new Color(0.74f, 0.73f, 0.7f), 0.05f, 0.5f);
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
        BuildToilet(cx + 1.7f, cz + 1.4f, 180f);
        BuildWashbasin(cx - 2.1f, cz - 1.6f, 90f);
        BuildBathtub(cx - 1.2f, cz + 1.8f, 0f);
    }

    private void BuildBedroom(float cx, float cz)
    {
        BuildBed(cx + 0.6f, cz + 0.4f, 180f);
        BuildWardrobe(cx + 2.3f, cz + 2.0f, 0f);
        BuildCabinet(cx - 2.2f, cz - 2.0f, 180f);
    }

    private void BuildLivingRoom(float cx, float cz)
    {
        BuildSofa(cx + 1.6f, cz + 1.6f, 180f);
        BuildTvUnit(cx - 1.8f, cz + 1.7f, 0f);
        BuildTable(cx - 0.4f, cz - 0.6f, 1.4f, 0.8f, 0f, 0.45f);
        CreateDecoCube("Rug", new Vector3(cx, 0.02f, cz - 0.3f), new Vector3(3.2f, 0.02f, 2.4f), new Color(0.68f, 0.55f, 0.44f));
        BuildPlant(cx - 2.3f, cz - 2.2f);
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
        Material wood = MakeMaterial(new Color(0.6f, 0.43f, 0.26f), 0.02f, 0.4f);

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
        Material fabric = MakeMaterial(new Color(0.4f, 0.47f, 0.53f), 0.02f, 0.45f);
        Material cushion = MakeMaterial(new Color(0.5f, 0.58f, 0.64f), 0.02f, 0.45f);

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
        Material body = MakeMaterial(new Color(0.5f, 0.35f, 0.2f), 0.02f, 0.4f);
        Material door = MakeMaterial(new Color(0.62f, 0.46f, 0.28f), 0.02f, 0.4f);
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
    private void BuildRoomFloor(float xMin, float xMax, float zMin, float zMax, Color color)
    {
        float cx = (xMin + xMax) * 0.5f;
        float cz = (zMin + zMax) * 0.5f;
        CreateDecoCube("Room Floor", new Vector3(cx, 0.005f, cz), new Vector3(xMax - xMin, 0.01f, zMax - zMin), color);
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
        }
        if (Input.GetKeyDown(KeyCode.R) && dialogueIndex < 0)
        {
            RestUntilMorning();
        }

        // 按住 Tab 唤出鼠标（松开自动收回），Esc 也可释放
        bool wantMouse = Input.GetKey(KeyCode.Tab);
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            SetCursorLock(false);
        }
        if (wantMouse)
        {
            if (cursorLocked)
            {
                SetCursorLock(false);
            }
        }
        else if (loggedIn && !cursorLocked && Input.GetMouseButtonDown(0) && !IsPointerOverGui(Input.mousePosition))
        {
            // WebGL 需要一次点击才能锁定鼠标
            SetCursorLock(true);
        }

        if (cursorLocked)
        {
            lookYaw += Input.GetAxis("Mouse X") * MouseSensitivity;
            lookPitch = Mathf.Clamp(lookPitch - Input.GetAxis("Mouse Y") * MouseSensitivity, -75f, 75f);
        }

        player.transform.rotation = Quaternion.Euler(0f, lookYaw, 0f);
        viewCamera.transform.position = playerPosition + Vector3.up * EyeHeight;
        viewCamera.transform.rotation = Quaternion.Euler(lookPitch, lookYaw, 0f);
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

    private void EnterGame(string accountName)
    {
        currentAccount = accountName;
        loggedIn = true;
        if (accountName != "游客")
        {
            AccountStore.UpdateAppearance(accountName, custCoat, custTrouser);
        }
        SetCursorLock(true);
        ShowToast("欢迎，" + accountName + "　·　按 B 看工具包，G 开商店，N 看日历账目", 7f);
    }

    private void TryLogin()
    {
        if (string.IsNullOrEmpty(loginUser))
        {
            loginMessage = "请输入用户名";
            return;
        }
        int coat, trouser;
        if (AccountStore.TryLoad(loginUser, loginPass, out coat, out trouser))
        {
            ApplyAppearance(coat, trouser);
            EnterGame(loginUser);
        }
        else
        {
            loginMessage = AccountStore.Exists(loginUser) ? "密码不正确" : "该用户名不存在，请先注册";
        }
    }

    private void TryRegister()
    {
        if (string.IsNullOrEmpty(loginUser) || loginPass.Length < 3)
        {
            loginMessage = "用户名不能为空，密码至少 3 位";
            return;
        }
        if (AccountStore.Exists(loginUser))
        {
            loginMessage = "该用户名已被注册";
            return;
        }
        AccountStore.Save(loginUser, loginPass, custCoat, custTrouser);
        loginMessage = "注册成功，已自动登录";
        EnterGame(loginUser);
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
        GUI.Label(new Rect(rect.x + 24f, rect.y + 46f, width - 48f, 20f), "本地账户（浏览器保存）　·　未登录无法开工", smallStyle);

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
            GUI.Label(new Rect(rect.x + 24f, y, 70f, 24f), "用户名", bodyStyle);
            loginUser = GUI.TextField(new Rect(rect.x + 96f, y - 2f, width - 130f, 28f), loginUser, 16);
            loginPass = GUI.PasswordField(new Rect(rect.x + 96f, y + 34f, width - 130f, 28f), loginPass, '*', 16);
            GUI.Label(new Rect(rect.x + 24f, y + 36f, 70f, 24f), "密码", bodyStyle);

            Rect action = new Rect(rect.x + 24f, y + 76f, width - 48f, 40f);
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

        footstepClip = CreateFootstepClip();
        voiceBlips = new AudioClip[5];
        float[] freqs = { 210f, 245f, 280f, 320f, 175f };
        for (int i = 0; i < voiceBlips.Length; i++)
        {
            voiceBlips[i] = CreateBlipClip(freqs[i], 0.11f);
        }
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

        Transform body = MakePrimitive(PrimitiveType.Cube, "Torso", root.transform, new Vector3(0f, 0.85f, 0f), new Vector3(0.5f, 0.7f, 0.3f), Quaternion.identity, clothMaterial).transform;
        MakePrimitive(PrimitiveType.Cube, "Head", body, new Vector3(0f, 0.52f, 0f), new Vector3(0.32f, 0.32f, 0.32f), Quaternion.identity, skinMaterial);
        MakePrimitive(PrimitiveType.Cube, "Helmet", body, new Vector3(0f, 0.72f, 0f), new Vector3(0.4f, 0.1f, 0.4f), Quaternion.identity, helmetMaterial);

        Transform leftArm = new GameObject("Left Arm Pivot").transform;
        leftArm.SetParent(root.transform, false);
        leftArm.localPosition = new Vector3(-0.34f, 1.12f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Arm", leftArm, new Vector3(0f, -0.3f, 0f), new Vector3(0.14f, 0.6f, 0.14f), Quaternion.identity, clothMaterial);

        Transform rightArm = new GameObject("Right Arm Pivot").transform;
        rightArm.SetParent(root.transform, false);
        rightArm.localPosition = new Vector3(0.34f, 1.12f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Arm", rightArm, new Vector3(0f, -0.3f, 0f), new Vector3(0.14f, 0.6f, 0.14f), Quaternion.identity, clothMaterial);

        // 每条腿分两段：髋枢轴（大腿）→ 膝枢轴（小腿），这样才能坐下
        Transform leftLeg = new GameObject("Left Leg Pivot").transform;
        leftLeg.SetParent(root.transform, false);
        leftLeg.localPosition = new Vector3(-0.13f, 0.62f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Thigh", leftLeg, new Vector3(0f, -0.15f, 0f), new Vector3(0.16f, 0.3f, 0.16f), Quaternion.identity, trouserMaterial);
        Transform leftKnee = new GameObject("Left Knee").transform;
        leftKnee.SetParent(leftLeg, false);
        leftKnee.localPosition = new Vector3(0f, -0.3f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Shin", leftKnee, new Vector3(0f, -0.15f, 0f), new Vector3(0.16f, 0.3f, 0.16f), Quaternion.identity, trouserMaterial);

        Transform rightLeg = new GameObject("Right Leg Pivot").transform;
        rightLeg.SetParent(root.transform, false);
        rightLeg.localPosition = new Vector3(0.13f, 0.62f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Thigh", rightLeg, new Vector3(0f, -0.15f, 0f), new Vector3(0.16f, 0.3f, 0.16f), Quaternion.identity, trouserMaterial);
        Transform rightKnee = new GameObject("Right Knee").transform;
        rightKnee.SetParent(rightLeg, false);
        rightKnee.localPosition = new Vector3(0f, -0.3f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Shin", rightKnee, new Vector3(0f, -0.15f, 0f), new Vector3(0.16f, 0.3f, 0.16f), Quaternion.identity, trouserMaterial);

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
            rig.body.localPosition = new Vector3(0f, 0.85f + Mathf.Abs(Mathf.Sin(t)) * 0.04f, 0f);
        }
        else
        {
            rig.leftArm.localRotation = Quaternion.identity;
            rig.rightArm.localRotation = Quaternion.identity;
            rig.leftLeg.localRotation = Quaternion.identity;
            rig.rightLeg.localRotation = Quaternion.identity;
            rig.body.localPosition = new Vector3(0f, 0.85f, 0f);
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
        bool blocked = repairingOrder != null || dialogueIndex >= 0;

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

        float t = Time.time * (running_ ? 12.5f : 9f);
        if (!grounded)
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
    private void InitializeOrders()
    {
        // 每个点位挂一个工程传感器（数字孪生的数据源），改造完成后读数回落到正常值
        // 参数：房型, 标题, 成因, 方案, 最低价, 最高价, x, z, 工具, 传感器, 单位, 正常值, 报警阈值, 量程
        // 厨房
        templates.Add(new OrderTemplate("厨房", "水槽下方渗漏", "水槽柜内给水角阀老化，柜底板见渗水痕迹", "更换角阀与存水弯，柜底增设防水托盘", 3200, 4200, -1.5f, 1.7f, ToolKind.Wrench,
            "柜内湿度", "%RH", 45f, 80f, 100f));
        templates.Add(new OrderTemplate("厨房", "灶台燃气管老化", "燃气软管超期服役，接口处有轻微泄漏", "更换不锈钢波纹管并做气密性检测", 2800, 3800, 1.4f, 1.7f, ToolKind.Wrench,
            "可燃气体浓度", "%LEL", 2f, 10f, 25f));
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
            "墙体含水率", "%", 8f, 18f, 35f));
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
        public int phase;      // 0 走向玩家 1 说明情况 2 离开
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
        public Transform root;
        public string[] lines;
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
            if (room.type == "公司")
            {
                continue;
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

        int cost = Mathf.RoundToInt(Random.Range(template.costMin, template.costMax + 1) / 100f) * 100;
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
                    // 玩家恰好在前台旁边的话可以聊两句，否则登记完就回家
                    bool playerNearby = Distance2D(playerPosition, owner.rig.root.position) < 3.5f;
                    if (playerNearby && dialogueIndex < 0 && talkTarget == null && !dialogueJustEnded)
                    {
                        owner.phase = 3;
                        owner.timer = 90f;
                        StartConversation(owner);
                    }
                    else
                    {
                        owner.phase = 2;
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
            state = OrderState.Pending
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
            float d = Distance2D(playerPosition, colleagues[i].root.position);
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
        repairingOrder.repairProgress = Mathf.Clamp01((float)repairClicks / RepairClicks);
        if (repairClicks >= RepairClicks)
        {
            CompleteRepair();
        }
    }

    private void CompleteRepair()
    {
        repairingOrder.repairProgress = 1f;
        repairingOrder.state = OrderState.Fixed;
        AddIncome(repairingOrder.cost);
        ShowToast("工单完成 " + repairingOrder.Code + " · " + repairingOrder.room + " " + repairingOrder.title + "（业主支付 ¥" + repairingOrder.cost.ToString("N0") + "）", 5f);

        repairingOrder = null;
        activeOrder = null;
        repairClicks = 0;
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
                order.tag.text = order.sensorName + "\n" + order.sensorValue.ToString("F1") + " " + order.sensorUnit + "  " + mark;
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
    private Rect BudgetRect { get { return new Rect(16f, 16f, 320f, 116f); } }
    private Rect TaskListRect
    {
        get
        {
            float height = taskListExpanded ? (104f + BuildDisplayList().Count * 66f) : 60f;
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
        DrawToolChip();
        DrawPromptPanel();
        DrawDialogue();
        DrawHintBar();
        DrawToast();
        DrawStartOverlay();
        DrawTransition();
        DrawLogin();
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
        GUI.Label(new Rect(rect.x + 27f, rect.y + 46f, 300f, 18f), ProjectSubtitle + "　·　" + currentRoomName, smallStyle);
        GUI.Label(new Rect(rect.x + 27f, rect.y + 64f, 300f, 18f), ClockText, smallStyle);
        Fill(new Rect(rect.x + 22f, rect.y + 86f, rect.width - 44f, 1f), dividerColor);
        GUI.Label(new Rect(rect.x + 22f, rect.y + 94f, 290f, 22f), "累计收入 ¥" + income.ToString("N0") + "    成本 ¥" + expenses.ToString("N0"), bodyStyle);
        GUI.Label(new Rect(rect.x + 22f, rect.y + 116f, 290f, 22f), "净利 ¥" + (income - expenses).ToString("N0") + "    现金 ¥" + Cash.ToString("N0"), bodyStyle);
    }

    private void DrawTaskList()
    {
        Rect rect = TaskListRect;
        DrawPanel(rect, panelFill, panelBorder);
        Fill(new Rect(rect.x + 12f, rect.y + 16f, 4f, 28f), fixedColor);
        GUI.Label(new Rect(rect.x + 26f, rect.y + 14f, 180f, 26f), "维修工单", titleStyle);
        GUI.Label(new Rect(rect.x + 27f, rect.y + 38f, 200f, 18f), CountActive() + " 进行中 · " + CountFixed() + " 已完工", smallStyle);

        // 折叠 / 展开
        Rect toggle = new Rect(rect.x + rect.width - 86f, rect.y + 16f, 70f, 28f);
        bool hoverToggle = toggle.Contains(Event.current.mousePosition);
        DrawPanel(toggle, hoverToggle ? Color.Lerp(btnBlue, Color.white, 0.15f) : btnBlue, Color.clear);
        if (GUI.Button(toggle, GUIContent.none, GUIStyle.none))
        {
            taskListExpanded = !taskListExpanded;
        }
        GUI.Label(toggle, taskListExpanded ? "收起" : "展开", cardButtonStyle);

        if (!taskListExpanded)
        {
            return;
        }

        float y = rect.y + 64f;
        Fill(new Rect(rect.x + 18f, y, rect.width - 36f, 1f), dividerColor);
        y += 10f;

        List<Order> display = BuildDisplayList();
        if (display.Count == 0)
        {
            GUI.Label(new Rect(rect.x + 18f, y + 6f, rect.width - 36f, 22f), "暂无工单，系统正在派单…", smallStyle);
            return;
        }

        for (int i = 0; i < display.Count; i++)
        {
            DrawOrderCard(new Rect(rect.x + 12f, y + i * 66f, rect.width - 24f, 58f), display[i]);
        }
    }

    private void DrawOrderCard(Rect card, Order order)
    {
        bool isActive = order == activeOrder || order == repairingOrder;
        DrawPanel(card, isActive ? new Color(1f, 1f, 1f, 0.11f) : new Color(1f, 1f, 1f, 0.04f), Color.clear);
        Fill(new Rect(card.x + 9f, card.y + 8f, 4f, card.height - 16f), stateColors[(int)order.state]);

        GUI.Label(new Rect(card.x + 22f, card.y + 4f, card.width - 32f, 20f),
            order.Code + "  " + order.room + " · " + order.title, cardTitleStyle);

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
        return order.state == OrderState.Fixed ? "已完工" : "待维修";
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
    private Rect TwinRect { get { return new Rect(Screen.width * 0.5f - 370f, 66f, 740f, 488f); } }
    // 收起时的紧凑状态条（顶部居中，不挡视野）
    private Rect TwinBarRect { get { return new Rect(Screen.width * 0.5f - 200f, 16f, 400f, 42f); } }

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
                "数字孪生监测　·　报警 " + alarms + " 个　·　已消除 " + CountFixed() + "/" + orders.Count, smallStyle);

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
        DrawPanel(rect, new Color(0.04f, 0.07f, 0.1f, 0.96f), new Color(0.45f, 0.75f, 0.9f, 0.35f));

        GUI.Label(new Rect(rect.x + 20f, rect.y + 14f, 420f, 28f), "数字孪生监测平台　·　厨房改造工程", titleStyle);
        GUI.Label(new Rect(rect.x + 21f, rect.y + 42f, 420f, 18f), ClockText + "　·　监测点位 " + orders.Count + " 个", smallStyle);

        Rect collapse = new Rect(rect.x + rect.width - 96f, rect.y + 16f, 76f, 30f);
        bool hoverCollapse = collapse.Contains(Event.current.mousePosition);
        Fill(collapse, hoverCollapse ? Color.Lerp(btnBlue, Color.white, 0.18f) : btnBlue);
        if (GUI.Button(collapse, GUIContent.none, GUIStyle.none))
        {
            twinPanelOpen = false;
        }
        GUI.Label(collapse, "收起 T", cardButtonStyle);

        // ── 实时数据表 ──
        float top = rect.y + 70f;
        GUI.Label(new Rect(rect.x + 20f, top, 300f, 20f), "① 现场传感器实时数据", cardTitleStyle);
        Fill(new Rect(rect.x + 20f, top + 22f, rect.width - 40f, 1f), dividerColor);

        GUI.Label(new Rect(rect.x + 20f, top + 28f, 70f, 18f), "编号", smallStyle);
        GUI.Label(new Rect(rect.x + 92f, top + 28f, 170f, 18f), "点位 / 传感器", smallStyle);
        GUI.Label(new Rect(rect.x + 268f, top + 28f, 110f, 18f), "实时值", smallStyle);
        GUI.Label(new Rect(rect.x + 388f, top + 28f, 90f, 18f), "报警阈值", smallStyle);
        GUI.Label(new Rect(rect.x + 486f, top + 28f, 90f, 18f), "状态", smallStyle);

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

        // ── KPI 与对比 ──
        float kpi = rect.y + 292f;
        GUI.Label(new Rect(rect.x + 20f, kpi, 300f, 20f), "② 工程 KPI 与方案对比", cardTitleStyle);
        Fill(new Rect(rect.x + 20f, kpi + 22f, rect.width - 40f, 1f), dividerColor);

        float lx = rect.x + 20f;
        float rx = rect.x + 400f;
        GUI.Label(new Rect(lx, kpi + 30f, 200f, 18f), "本平台（数字孪生）", cardTitleStyle);
        GUI.Label(new Rect(rx, kpi + 30f, 200f, 18f), "传统人工巡检", cardTitleStyle);

        string[] labels = { "隐患消除率", "平均处置时长", "漏检率" };
        float clearRate = HazardClearRate();
        float avgHours = AverageResponseHours();
        string[] digital = { clearRate.ToString("F0") + " %", (avgHours <= 0f ? "—" : avgHours.ToString("F1") + " h"), "3 %" };
        string[] legacy = { "68 %", LegacyResponseHours().ToString("F0") + " h", LegacyMissRate().ToString("F0") + " %" };

        for (int i = 0; i < labels.Length; i++)
        {
            float ry2 = kpi + 54f + i * 24f;
            GUI.Label(new Rect(lx, ry2, 200f, 18f), labels[i], smallStyle);
            Color prev = GUI.color;
            GUI.color = new Color(0.6f, 0.92f, 0.75f);
            GUI.Label(new Rect(lx + 120f, ry2, 140f, 18f), digital[i], smallStyle);
            GUI.color = new Color(0.85f, 0.72f, 0.6f);
            GUI.Label(new Rect(rx + 120f, ry2, 140f, 18f), legacy[i], smallStyle);
            GUI.color = prev;
        }

        GUI.Label(new Rect(rx, kpi + 54f + 3 * 24f, 360f, 18f),
            "旧房改造行业人工巡检基准值（用于对比分析）", smallStyle);

        // ── 累计经营 ──
        float biz = kpi + 150f;
        Fill(new Rect(rect.x + 20f, biz, rect.width - 40f, 1f), dividerColor);
        GUI.Label(new Rect(rect.x + 20f, biz + 8f, 300f, 18f),
            "累计改造投入 ¥" + expenses.ToString("N0")
            + "　　业主支付 ¥" + income.ToString("N0")
            + "　　净利 ¥" + (income - expenses).ToString("N0"), bodyStyle);
        GUI.Label(new Rect(rect.x + 20f, biz + 30f, 520f, 18f),
            "已完成点位 " + CountFixed() + " / " + orders.Count
            + "　　数据回归正常 " + CountSensorNormal() + " 个", smallStyle);

        Rect export = new Rect(rect.x + rect.width - 200f, biz + 4f, 180f, 34f);
        bool hoverExport = export.Contains(Event.current.mousePosition);
        Fill(export, hoverExport ? Color.Lerp(btnBlue, Color.white, 0.18f) : btnBlue);
        if (GUI.Button(export, GUIContent.none, GUIStyle.none))
        {
            ExportReport();
        }
        GUI.Label(export, "导出验收报告", cardButtonStyle);

        GUI.Label(new Rect(rect.x + 20f, biz + 52f, 700f, 18f),
            "改造后传感器读数回落至正常区间，全过程数据可追溯", smallStyle);
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
        sb.AppendLine();
        sb.AppendLine("二、隐患清单与处置记录");
        sb.AppendLine("  编号   点位                     传感器            报警值      阈值      处置经费");
        for (int i = 0; i < orders.Count; i++)
        {
            Order o = orders[i];
            string state = o.state == OrderState.Fixed ? "已消除" : "未处置";
            sb.AppendLine("  " + o.Code + "  " + Pad(o.room, 22) + "  " + Pad(o.sensorName, 16)
                + "  " + Pad(o.sensorValue.ToString("F1") + o.sensorUnit, 10)
                + "  " + Pad(">" + o.sensorAlarm.ToString("F1"), 8)
                + "  " + (o.state == OrderState.Fixed ? "¥" + o.cost.ToString("N0") : "—")
                + "  [" + state + "]");
            if (o.state == OrderState.Fixed)
            {
                sb.AppendLine("        成因：" + o.cause);
                sb.AppendLine("        方案：" + o.plan);
            }
        }
        sb.AppendLine();
        sb.AppendLine("三、关键指标对比分析");
        sb.AppendLine("  指标              本平台(数字孪生)     传统人工巡检     改善");
        sb.AppendLine("  隐患消除率        " + Pad(HazardClearRate().ToString("F0") + " %", 20) + Pad("68 %", 17) + "显著提升");
        sb.AppendLine("  平均处置时长      " + Pad((AverageResponseHours() <= 0f ? "—" : AverageResponseHours().ToString("F1") + " h"), 20) + Pad(LegacyResponseHours().ToString("F0") + " h", 17) + "明显缩短");
        sb.AppendLine("  漏检率            " + Pad("3 %", 20) + Pad(LegacyMissRate().ToString("F0") + " %", 17) + "大幅下降");
        sb.AppendLine("  改造成本          " + Pad("¥" + expenses.ToString("N0"), 20) + Pad("×" + LegacyCostFactor().ToString("F2"), 17) + "成本更优");
        sb.AppendLine();
        sb.AppendLine("四、经营数据");
        sb.AppendLine("  累计改造投入：" + "¥" + expenses.ToString("N0"));
        sb.AppendLine("  业主支付合计：" + "¥" + income.ToString("N0"));
        sb.AppendLine("  净利　　　　：" + "¥" + (income - expenses).ToString("N0"));
        sb.AppendLine();
        sb.AppendLine("五、结论");
        sb.AppendLine("  本平台以传感器实时数据驱动隐患排查与处置，隐患消除率、平均处置时长、");
        sb.AppendLine("  漏检率等关键指标均优于传统人工巡检方式，实现了改造过程的数据可追溯。");
        sb.AppendLine("==============================================");
        return sb.ToString();
    }

    private static string Pad(string text, int width)
    {
        if (text == null)
        {
            text = string.Empty;
        }
        // 中文按两个字符宽度估算，保证导出的文本表格对齐
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
        GUI.Label(rect, "WASD 移动　·　Shift 加速　·　空格 跳跃　·　左键 现场施工　·　E 对话　·　Q/滚轮 换工具　·　F 开关门　·　T 监测平台　·　G 商店　·　B 工具包　·　N 日历账目　·　M 地图　·　R 夜间休息　·　Tab 唤出鼠标", centerStyle);
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
            || (twinPanelOpen ? TwinRect.Contains(point) : TwinBarRect.Contains(point)) || (!loggedIn);
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
