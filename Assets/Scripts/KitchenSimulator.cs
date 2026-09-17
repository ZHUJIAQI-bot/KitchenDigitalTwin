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
    private const int TotalBudget = 30000;
    private const float MoveSpeed = 3.6f;
    private const float InteractDistance = 1.6f;
    private const float RepairDuration = 2.4f;
    private const float MarkerHeight = 1.95f;
    private const float PlayerRadius = 0.34f;

    // 派单节奏
    private const float OrderIntervalMin = 9f;
    private const float OrderIntervalMax = 16f;
    private const int MaxActiveOrders = 4;
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
        public Renderer[] renderers;
        public float repairProgress;
        public bool needsRebuild;

        public string Code { get { return "#" + id.ToString("D3"); } }
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

        public OrderTemplate(string roomType, string title, string cause, string plan, int costMin, int costMax, float dx, float dz)
        {
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

        public ToolInfo(string name, ToolKind kind, Color color)
        {
            this.name = name;
            this.kind = kind;
            this.color = color;
        }
    }

    // 第一人称手持工具（视图模型）
    private Transform toolPivot;
    private float toolAnim;
    private bool walking;

    // 跳跃
    private float verticalVelocity;
    private bool grounded = true;

    // 工具背包
    private readonly List<ToolInfo> tools = new List<ToolInfo>();
    private int currentTool;
    private bool bagOpen;

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
    private float stepTimer;

    private readonly Vector3 spawnPosition = new Vector3(-15f, GroundLevel, -3f);

    // 开场 NPC 对话
    private class DialogueLine
    {
        public string speaker;
        public string text;
    }
    private readonly List<DialogueLine> dialogue = new List<DialogueLine>();
    private int dialogueIndex = -1;
    private bool introDone;
    private float introDelay = 1.2f;
    private Transform npcTransform;

    // 小地图
    private const float WorldMinX = -22f;
    private const float WorldMaxX = 64f;
    private const float WorldMinZ = -20f;
    private const float WorldMaxZ = 10f;

    private Order activeOrder;
    private Order repairingOrder;
    private int orderSerial;
    private float orderTimer;
    private bool taskListExpanded = true;
    private int spent;
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

    private int Remaining { get { return TotalBudget - spent; } }

    private string startError;

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

        // 相机最先创建：即使后续初始化抛异常，也能渲染出画面而不是全黑
        BuildCamera();

        try
        {
            BuildMaterials();
            BuildWorld();
            InitializeOrders();
            BuildPlayer();
            BuildNpc();
            BuildIntroDialogue();
            BuildTools();
            BuildAudio();
        }
        catch (System.Exception e)
        {
            startError = e.GetType().Name + ": " + e.Message;
            Debug.LogError("初始化失败：" + e);
        }
    }

    private void Update()
    {
        HandleLook();
        HandleMovement();
        HandleDialogue();
        UpdateDoors();
        UpdateOrderSpawning();
        DetectInteraction();
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
        glassMaterial = MakeTransparent(new Color(0.72f, 0.88f, 0.95f), 0.42f, 0.92f);
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
        sun.intensity = 1.15f;
        sun.color = new Color(1f, 0.96f, 0.86f);
        sun.shadows = LightShadows.Soft;
        sunObject.transform.rotation = Quaternion.Euler(46f, -38f, 0f);
        generatedObjects.Add(sunObject);

        BuildOutdoor();
        BuildCompany();
        BuildHouse();
    }

    // ── 室外小区环境 ──────────────────────────────────────
    private void BuildOutdoor()
    {
        // 各层顶面高度严格错开，避免共面导致的 z-fighting 闪烁
        CreateDecoCube("Lawn", new Vector3(20f, -0.27f, 0f), new Vector3(200f, 0.5f, 140f), new Color(0.38f, 0.6f, 0.3f));

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
        { -20f, -8f, -7f, 5f },   // 公司
        { 2f, 14f, -7f, 5f },     // 1号楼
        { 18f, 30f, -7f, 5f },    // 2号楼
        { 34f, 46f, -7f, 5f },    // 3号楼
        { 50f, 62f, -7f, 5f },    // 4号楼
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
            doorStart, doorEnd, 0f, 2.4f);
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
        CreateDecoCube("Sign Board", new Vector3(-16.4f, 2.75f, z0 - 0.25f), new Vector3(5.4f, 0.9f, 0.12f), new Color(0.13f, 0.32f, 0.5f));
        CreateWorldLabel("焕新维修公司", new Vector3(-16.4f, 2.75f, z0 - 0.33f), 0.36f, Color.white);

        CreateDecoCube("Price Board", new Vector3(-12.6f, 1.6f, z0 - 0.25f), new Vector3(3.4f, 2.2f, 0.1f), new Color(0.93f, 0.92f, 0.88f));
        CreateDecoCube("Price Board Frame", new Vector3(-12.6f, 1.6f, z0 - 0.21f), new Vector3(3.6f, 2.4f, 0.06f), new Color(0.35f, 0.28f, 0.2f));
        CreateWorldLabel("维 修 价 目 表\n──────────\n水路渗漏  ¥3200\n电路检修  ¥2600\n燃气管道  ¥4600\n墙面翻新  ¥2800\n地面空鼓  ¥2200\n门窗调整  ¥1200",
            new Vector3(-12.6f, 1.6f, z0 - 0.3f), 0.16f, new Color(0.15f, 0.15f, 0.18f));

        // 室内陈设
        BuildDeskStation(-15f, -4.2f, 180f, false);
        BuildDeskStation(-11.8f, -4.2f, 180f, true);
        BuildCabinet(-19f, 3.4f, 0f);
        BuildPlant(-19.2f, -6f);
        BuildSofa(-9.6f, 2.6f, 270f);
        BuildPlant(-9.5f, -5.6f);

        // 接待台
        AddSolidBox("Reception", new Vector3(-13.2f, 0.5f, -5.6f), new Vector3(3.2f, 1f, 0.8f), new Color(0.55f, 0.38f, 0.24f));
        CreateDecoCube("Reception Top", new Vector3(-13.2f, 1.03f, -5.6f), new Vector3(3.4f, 0.08f, 0.95f), new Color(0.78f, 0.76f, 0.72f));

        // 吸顶灯
        BuildCeilingLight(-16f, -1f);
        BuildCeilingLight(-12f, -1f);
        BuildCeilingLight(-16f, 3f);
        BuildCeilingLight(-12f, 3f);
        AddRoomLight(-14f, -1f, 16f);

        rooms.Add(new Room { name = "装修公司", type = "公司", center = new Vector3(-14f, 0f, -1f), xMin = x0, xMax = x1, zMin = z0, zMax = z1 });

        BuildSensorDoor(doorStart, doorEnd, z0);
    }

    // 感应式透明玻璃门：玩家靠近自动打开
    private void BuildSensorDoor(float start, float end, float z)
    {
        Material frame = MakeMaterial(new Color(0.42f, 0.44f, 0.47f), 0.7f, 0.7f);
        Material glass = MakeTransparent(new Color(0.75f, 0.9f, 0.96f), 0.35f, 0.95f);
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
        light.range = range;
        light.intensity = 1.1f;
        light.color = new Color(1f, 0.94f, 0.85f);
        generatedObjects.Add(lightObject);
    }

    private void CreateWorldLabel(string text, Vector3 position, float size, Color color)
    {
        GameObject label = new GameObject("Label");
        label.transform.SetParent(transform, false);
        label.transform.position = position;
        label.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

        TextMesh mesh = label.AddComponent<TextMesh>();
        mesh.font = UiFont;
        mesh.text = text;
        mesh.fontSize = 64;
        mesh.characterSize = size;
        mesh.anchor = TextAnchor.MiddleCenter;
        mesh.alignment = TextAlignment.Center;
        mesh.color = color;
        mesh.lineSpacing = 1.1f;
        if (mesh.font != null)
        {
            Renderer renderer = label.GetComponent<Renderer>();
            renderer.sharedMaterial = mesh.font.material;
        }
        generatedObjects.Add(label);
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
        float[] origins = { 2f, 18f, 34f, 50f };
        Color[] roofColors =
        {
            new Color(0.55f, 0.33f, 0.27f),
            new Color(0.36f, 0.44f, 0.42f),
            new Color(0.5f, 0.4f, 0.28f),
            new Color(0.42f, 0.38f, 0.48f),
        };
        for (int i = 0; i < origins.Length; i++)
        {
            BuildResidence(i + 1, origins[i], roofColors[i]);
        }
    }

    // 单层住宅：12×12，四个 6×6 房间
    //   前左 客厅（入户）/ 前右 厨房
    //   后左 卫生间       / 后右 卧室
    private void BuildResidence(int index, float x0, Color roofColor)
    {
        const float depth = 12f;
        float x1 = x0 + 6f, x2 = x0 + 12f;
        float z0 = -7f, zMid = -1f, z1 = 5f;
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

        // 房间
        AddRoom(tag + " 客厅", "客厅", x0, x1, z0, zMid);
        AddRoom(tag + " 厨房", "厨房", x1, x2, z0, zMid);
        AddRoom(tag + " 卫生间", "卫生间", x0, x1, zMid, z1);
        AddRoom(tag + " 卧室", "卧室", x1, x2, zMid, z1);

        BuildLivingRoom(x0 + 3f, z0 + 3f);
        BuildKitchenRoom(x1 + 3f, z0 + 3f);
        BuildBathroom(x0 + 3f, zMid + 3f);
        BuildBedroom(x1 + 3f, zMid + 3f);

        BuildCeilingLight(x0 + 3f, z0 + 3f);
        BuildCeilingLight(x1 + 3f, z0 + 3f);
        BuildCeilingLight(x0 + 3f, zMid + 3f);
        BuildCeilingLight(x1 + 3f, zMid + 3f);
        AddRoomLight(x0 + 6f, zMid - 1f, 15f);

        BuildHouseDoor(tag + " Door", x0 + 2f, x0 + 4.2f, z0);
    }

    private void AddRoom(string name, string type, float xMin, float xMax, float zMin, float zMax)
    {
        rooms.Add(new Room
        {
            name = name,
            type = type,
            center = new Vector3((xMin + xMax) * 0.5f, 0f, (zMin + zMax) * 0.5f),
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
        else if (!cursorLocked && Input.GetMouseButtonDown(0) && !IsPointerOverGui(Input.mousePosition))
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

    // ── 玩家 ──────────────────────────────────────────────
    private void BuildPlayer()
    {
        player = new GameObject("Inspector");
        playerPosition = spawnPosition;
        player.transform.position = spawnPosition;
        player.transform.rotation = Quaternion.identity;
        lookYaw = 0f;

        Material bodyMaterial = MakeMaterial(new Color(0.16f, 0.34f, 0.48f), 0.05f, 0.35f);
        Material legMaterial = MakeMaterial(new Color(0.22f, 0.26f, 0.3f), 0.05f, 0.3f);

        // 身体与四肢在世界中可见（低头能看见），头部隐藏避免遮挡视线
        playerBody = MakePrimitive(PrimitiveType.Cube, "Torso", player.transform, new Vector3(0f, 0.85f, 0f), new Vector3(0.5f, 0.7f, 0.3f), Quaternion.identity, bodyMaterial).transform;

        leftArmPivot = new GameObject("Left Arm Pivot").transform;
        leftArmPivot.SetParent(player.transform, false);
        leftArmPivot.localPosition = new Vector3(-0.32f, 1.1f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Left Arm", leftArmPivot, new Vector3(0f, -0.28f, 0f), new Vector3(0.14f, 0.56f, 0.14f), Quaternion.identity, bodyMaterial);
        rightArmPivot = new GameObject("Right Arm Pivot").transform;
        rightArmPivot.SetParent(player.transform, false);
        rightArmPivot.localPosition = new Vector3(0.32f, 1.1f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Right Arm", rightArmPivot, new Vector3(0f, -0.28f, 0f), new Vector3(0.14f, 0.56f, 0.14f), Quaternion.identity, bodyMaterial);

        leftLegPivot = new GameObject("Left Leg Pivot").transform;
        leftLegPivot.SetParent(player.transform, false);
        leftLegPivot.localPosition = new Vector3(-0.13f, 0.68f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Left Leg", leftLegPivot, new Vector3(0f, -0.28f, 0f), new Vector3(0.16f, 0.56f, 0.16f), Quaternion.identity, legMaterial);
        rightLegPivot = new GameObject("Right Leg Pivot").transform;
        rightLegPivot.SetParent(player.transform, false);
        rightLegPivot.localPosition = new Vector3(0.13f, 0.68f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Right Leg", rightLegPivot, new Vector3(0f, -0.28f, 0f), new Vector3(0.16f, 0.56f, 0.16f), Quaternion.identity, legMaterial);

        BuildViewmodel();
    }

    // ── 工具背包 ──────────────────────────────────────────
    private void BuildTools()
    {
        tools.Add(new ToolInfo("电动起子", ToolKind.Drill, new Color(0.85f, 0.35f, 0.12f)));
        tools.Add(new ToolInfo("螺丝刀", ToolKind.Screwdriver, new Color(0.85f, 0.72f, 0.1f)));
        tools.Add(new ToolInfo("活动扳手", ToolKind.Wrench, new Color(0.6f, 0.62f, 0.66f)));
        tools.Add(new ToolInfo("羊角锤", ToolKind.Hammer, new Color(0.45f, 0.47f, 0.5f)));
        tools.Add(new ToolInfo("剪刀", ToolKind.Scissors, new Color(0.75f, 0.76f, 0.8f)));
        tools.Add(new ToolInfo("防水胶布", ToolKind.Tape, new Color(0.15f, 0.15f, 0.16f)));
        tools.Add(new ToolInfo("测电笔", ToolKind.Tester, new Color(0.9f, 0.25f, 0.2f)));
        currentTool = 0;
        BuildToolModel();
    }

    private void NextTool(int delta)
    {
        if (tools.Count == 0)
        {
            return;
        }
        currentTool = (currentTool + delta) % tools.Count;
        if (currentTool < 0)
        {
            currentTool += tools.Count;
        }
        BuildToolModel();
        ShowToast("切换工具：" + tools[currentTool].name, 2f);
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
        npcTransform = BuildCharacterModel("Boss", new Vector3(-14.5f, 0f, -1.2f), 196f,
            new Color(0.62f, 0.3f, 0.22f), new Color(0.83f, 0.66f, 0.5f));
    }

    private Transform BuildCharacterModel(string name, Vector3 position, float yaw, Color cloth, Color skin)
    {
        GameObject root = new GameObject(name);
        root.transform.SetParent(transform, false);
        root.transform.position = position;
        root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

        Material clothMaterial = MakeMaterial(cloth, 0.05f, 0.35f);
        Material skinMaterial = MakeMaterial(skin, 0.02f, 0.3f);
        Material trouserMaterial = MakeMaterial(new Color(0.22f, 0.26f, 0.3f), 0.05f, 0.3f);
        Material helmetMaterial = MakeMaterial(new Color(0.95f, 0.72f, 0.12f), 0.1f, 0.45f);

        MakePrimitive(PrimitiveType.Cube, "Torso", root.transform, new Vector3(0f, 0.85f, 0f), new Vector3(0.5f, 0.7f, 0.3f), Quaternion.identity, clothMaterial);
        MakePrimitive(PrimitiveType.Cube, "Head", root.transform, new Vector3(0f, 1.37f, 0f), new Vector3(0.32f, 0.32f, 0.32f), Quaternion.identity, skinMaterial);
        MakePrimitive(PrimitiveType.Cube, "Helmet", root.transform, new Vector3(0f, 1.57f, 0f), new Vector3(0.4f, 0.1f, 0.4f), Quaternion.identity, helmetMaterial);
        MakePrimitive(PrimitiveType.Cube, "Left Arm", root.transform, new Vector3(-0.34f, 0.8f, 0f), new Vector3(0.14f, 0.6f, 0.14f), Quaternion.identity, clothMaterial);
        MakePrimitive(PrimitiveType.Cube, "Right Arm", root.transform, new Vector3(0.34f, 0.8f, 0f), new Vector3(0.14f, 0.6f, 0.14f), Quaternion.identity, clothMaterial);
        MakePrimitive(PrimitiveType.Cube, "Left Leg", root.transform, new Vector3(-0.13f, 0.3f, 0f), new Vector3(0.16f, 0.6f, 0.16f), Quaternion.identity, trouserMaterial);
        MakePrimitive(PrimitiveType.Cube, "Right Leg", root.transform, new Vector3(0.13f, 0.3f, 0f), new Vector3(0.16f, 0.6f, 0.16f), Quaternion.identity, trouserMaterial);

        Collider[] colliders = root.GetComponentsInChildren<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            colliders[i].enabled = false;
        }
        return root.transform;
    }

    private void BuildIntroDialogue()
    {
        dialogue.Add(new DialogueLine { speaker = "工头 老张", text = "小陈，来活儿了。城东那户老房子问题一堆，业主催得紧。" });
        dialogue.Add(new DialogueLine { speaker = "工头 老张", text = "单子我都派进你系统了 —— 谁家有毛病、在哪个屋，工单上写着。" });
        dialogue.Add(new DialogueLine { speaker = "工头 老张", text = "带上工具去现场，走到问题跟前按 E 就能开工，修完记得登记费用。" });
        dialogue.Add(new DialogueLine { speaker = "工头 老张", text = "预算三万，省着点花。去吧！" });
    }

    private void HandleDialogue()
    {
        if (introDone)
        {
            return;
        }

        if (dialogueIndex < 0)
        {
            introDelay -= Time.deltaTime;
            if (introDelay <= 0f)
            {
                dialogueIndex = 0;
                SpeakBlip();
            }
            return;
        }

        // 说话时按节奏"嘟嘟"（模仿戴夫那类卡通配音）
        voiceTimer -= Time.deltaTime;
        if (voiceTimer <= 0f)
        {
            SpeakBlip();
        }

        if (Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Space))
        {
            dialogueIndex++;
            SpeakBlip();
            if (dialogueIndex >= dialogue.Count)
            {
                dialogueIndex = -1;
                introDone = true;
                orderTimer = 1.5f;
                ShowToast("系统自动派单中：新工单会随机出现在各房间，走近红色感叹号按 E 维修", 7f);
            }
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

        Vector3 input = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
        input = Vector3.ClampMagnitude(input, 1f);
        bool moving = input.sqrMagnitude > 0.01f && !blocked;

        if (moving)
        {
            // 以视角朝向为基准移动
            Vector3 direction = Quaternion.Euler(0f, lookYaw, 0f) * input.normalized;
            Vector3 move = direction * MoveSpeed * Time.deltaTime;
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
                stepTimer = StepInterval;
                footstepSource.pitch = Random.Range(0.9f, 1.1f);
                footstepSource.PlayOneShot(footstepClip, 0.5f);
            }
        }
        else
        {
            stepTimer = 0f;
        }
    }

    private bool Collides(Vector3 position)
    {
        Vector2 p = new Vector2(position.x, position.z);
        for (int i = 0; i < obstacles.Count; i++)
        {
            Bounds b = obstacles[i];
            float closestX = Mathf.Clamp(p.x, b.min.x, b.max.x);
            float closestZ = Mathf.Clamp(p.y, b.min.z, b.max.z);
            float dx = p.x - closestX;
            float dz = p.y - closestZ;
            if (dx * dx + dz * dz < PlayerRadius * PlayerRadius)
            {
                return true;
            }
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
        return false;
    }

    // 行走摆臂 + 手持工具动作
    private void UpdateAnimate()
    {
        if (playerBody == null)
        {
            return;
        }

        float t = Time.time * 9f;
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
            float swing = Mathf.Sin(t) * 26f;
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
            // 作业：机身前后推动 + 轻微旋转抖动
            float t = Time.time * 16f;
            toolAnim = Mathf.Lerp(toolAnim, 1f, Time.deltaTime * 6f);
            float push = (Mathf.Sin(t) * 0.5f + 0.5f) * 0.09f;
            float shake = Mathf.Sin(t * 2.4f) * 3f;
            toolPivot.localPosition = basePosition + new Vector3(0f, push * 0.35f, push) + new Vector3(0f, 0f, 0f);
            toolPivot.localRotation = Quaternion.Euler(baseEuler.x + push * 90f, baseEuler.y, baseEuler.z + shake);
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
        // 偏移量相对房间中心，房间为 6×6
        // 厨房
        templates.Add(new OrderTemplate("厨房", "水槽下方渗漏", "水槽柜内给水角阀老化，柜底板见渗水痕迹", "更换角阀与存水弯，柜底增设防水托盘", 3200, 4200, -1.5f, 1.7f));
        templates.Add(new OrderTemplate("厨房", "灶台燃气管老化", "燃气软管超期服役，接口处有轻微泄漏", "更换不锈钢波纹管并做气密性检测", 2800, 3800, 1.4f, 1.7f));
        templates.Add(new OrderTemplate("厨房", "橱柜门板变形", "地柜门板受潮变形，开合卡顿异响", "更换门板并调整铰链，柜体做防潮处理", 1200, 2000, 0f, 1.1f));
        templates.Add(new OrderTemplate("厨房", "冰箱插座接触不良", "冰箱专用插座松动，插头发热变色", "更换 16A 插座面板并紧固线路", 900, 1600, 1.9f, -1.6f));

        // 客厅
        templates.Add(new OrderTemplate("客厅", "地面瓷砖空鼓", "地面瓷砖局部空鼓脱层，踩踏有松动异响", "空鼓砖拆除重铺，基层找平做界面处理", 1800, 2800, 0f, -0.6f));
        templates.Add(new OrderTemplate("客厅", "沙发背景墙开裂", "背景墙基层开裂，饰面起皮脱落", "铲除空鼓层，挂网后重新批刮饰面", 2200, 3200, 1.9f, 1.5f));
        templates.Add(new OrderTemplate("客厅", "电视线缆外露", "电视墙线缆杂乱外露，存在安全隐患", "加装线槽归拢线缆并做隐蔽处理", 700, 1300, -1.9f, 1.5f));
        templates.Add(new OrderTemplate("客厅", "吊顶灯带脱落", "吊顶灯带卡扣老化脱落，线路外露", "更换卡扣并整理线路，加装线槽", 1000, 1800, 0f, -2.0f));

        // 卧室
        templates.Add(new OrderTemplate("卧室", "木门变形关不严", "木门受潮膨胀变形，闭合困难漏风", "刨修门边并调整铰链，门扇做防潮封边", 1200, 2000, -1.9f, 1.7f));
        templates.Add(new OrderTemplate("卧室", "墙面返潮发霉", "外墙渗水导致内墙返潮霉变", "外墙迎水面重做防水，内墙铲除后批耐水腻子", 2800, 3800, 1.9f, 0.5f));
        templates.Add(new OrderTemplate("卧室", "衣柜滑轨卡顿", "衣柜推拉门滑轨变形积尘，推拉困难", "更换滑轨并调整门扇垂直度", 600, 1200, 1.9f, 1.7f));
        templates.Add(new OrderTemplate("卧室", "床头插座松动", "床头插座面板松动，插拔打火", "更换面板并加固暗盒", 800, 1400, 0f, -1.7f));

        // 卫生间
        templates.Add(new OrderTemplate("卫生间", "地漏返味", "地漏存水弯干涸失效，下水道异味返涌", "更换防臭地漏芯，补做存水弯", 800, 1400, 0f, -1.6f));
        templates.Add(new OrderTemplate("卫生间", "墙面瓷砖空鼓", "淋浴区瓷砖空鼓脱层，存在脱落风险", "空鼓砖拆除重贴，基层做防水处理", 2200, 3200, -1.9f, 0.4f));
        templates.Add(new OrderTemplate("卫生间", "马桶底座渗水", "马桶法兰密封圈老化，底座渗水返碱", "更换法兰密封圈并重新打胶固定", 1500, 2400, 1.7f, 1.4f));

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

        if (CountActive() < MaxActiveOrders && TrySpawnOrder())
        {
            orderTimer = Random.Range(OrderIntervalMin, OrderIntervalMax);
        }
        else
        {
            orderTimer = 1.5f; // 满单或无可派位置，稍后重试
        }
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

        GameObject bar = MakePrimitive(PrimitiveType.Cube, "Bar", marker.transform, new Vector3(0f, 0.34f, 0f), new Vector3(0.09f, 0.3f, 0.09f), Quaternion.identity, stateMaterials[0]);
        GameObject dot = MakePrimitive(PrimitiveType.Sphere, "Dot", marker.transform, new Vector3(0f, 0.06f, 0f), Vector3.one * 0.14f, Quaternion.identity, stateMaterials[0]);
        GameObject ring = CreateCylinder("Ring", new Vector3(order.site.x, 0.02f, order.site.z), 0.28f, 0.016f, Quaternion.identity, stateMaterials[0]);
        GameObject beam = CreateCylinder("Beam", new Vector3(order.site.x, MarkerHeight * 0.5f, order.site.z), 0.014f, MarkerHeight, Quaternion.identity, stateMaterials[0]);
        ring.transform.SetParent(marker.transform, true);
        beam.transform.SetParent(marker.transform, true);

        order.marker = marker;
        order.renderers = new[]
        {
            bar.GetComponent<Renderer>(),
            dot.GetComponent<Renderer>(),
            ring.GetComponent<Renderer>(),
            beam.GetComponent<Renderer>()
        };
    }

    // ── 交互与维修 ────────────────────────────────────────
    private void DetectInteraction()
    {
        if (repairingOrder != null || dialogueIndex >= 0)
        {
            activeOrder = null;
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

        if (activeOrder != null && Input.GetKeyDown(KeyCode.E))
        {
            StartRepair(activeOrder);
        }
    }

    private void StartRepair(Order order)
    {
        repairingOrder = order;
        order.state = OrderState.Repairing;
        order.repairProgress = 0f;

        Vector3 direction = order.site - playerPosition;
        direction.y = 0f;
        if (direction.sqrMagnitude > 0.001f)
        {
            player.transform.forward = direction.normalized;
        }

        ShowToast("开始维修 " + order.Code + " · " + order.room + " · " + order.title, 3f);
    }

    private void UpdateRepair()
    {
        if (repairingOrder == null)
        {
            return;
        }

        repairingOrder.repairProgress += Time.deltaTime / RepairDuration;
        if (repairingOrder.repairProgress >= 1f)
        {
            repairingOrder.repairProgress = 1f;
            repairingOrder.state = OrderState.Fixed;
            spent += repairingOrder.cost;
            ShowToast("工单完成 " + repairingOrder.Code + " · " + repairingOrder.room + " " + repairingOrder.title + "（¥" + repairingOrder.cost.ToString("N0") + "）", 5f);

            repairingOrder = null;
            activeOrder = null;
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
            float height = taskListExpanded ? (104f + BuildDisplayList().Count * 52f) : 60f;
            return new Rect(Screen.width - 348f, 16f, 332f, height);
        }
    }
    private Rect PromptRect { get { return new Rect(16f, Screen.height - 152f, 430f, 112f); } }
    private Rect ToolChipRect { get { return new Rect(16f, Screen.height - 196f, 340f, 36f); } }
    private Rect BagRect { get { return new Rect(16f, 146f, 288f, 56f + tools.Count * 32f); } }
    private Rect HintRect { get { return new Rect(0f, Screen.height - 30f, Screen.width, 30f); } }

    private void OnGUI()
    {
        EnsureStyles();
        DrawMinimap();
        DrawBudgetPanel();
        DrawTaskList();
        DrawBag();
        DrawToolChip();
        DrawPromptPanel();
        DrawDialogue();
        DrawHintBar();
        DrawToast();
        DrawStartOverlay();
        DrawStartError();
    }

    // ── 小地图 ────────────────────────────────────────────
    private Rect MinimapRect { get { return new Rect(Screen.width - 316f, Screen.height - 200f, 300f, 138f); } }

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
        GUI.Label(new Rect(rect.x + 12f, rect.y + 6f, 160f, 22f), "现场平面图", cardTitleStyle);

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
    private void DrawDialogue()
    {
        if (dialogueIndex < 0 || dialogueIndex >= dialogue.Count)
        {
            return;
        }

        DialogueLine line = dialogue[dialogueIndex];
        float width = Mathf.Min(Screen.width - 120f, 720f);
        Rect rect = new Rect((Screen.width - width) * 0.5f, Screen.height - 150f, width, 104f);
        DrawPanel(rect, panelFill, panelBorder);
        Fill(new Rect(rect.x + 12f, rect.y + 14f, 4f, rect.height - 28f), btnBlue);

        GUI.Label(new Rect(rect.x + 28f, rect.y + 12f, rect.width - 56f, 24f), line.speaker, cardTitleStyle);
        GUI.Label(new Rect(rect.x + 28f, rect.y + 38f, rect.width - 56f, 44f), line.text, bodyStyle);
        GUI.Label(new Rect(rect.x + rect.width - 150f, rect.y + rect.height - 26f, 130f, 20f), "按 E 继续 (" + (dialogueIndex + 1) + "/" + dialogue.Count + ")", smallStyle);
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
        GUI.Label(new Rect(rect.x + 27f, rect.y + 46f, 280f, 20f), ProjectSubtitle + "　·　当前：" + currentRoomName, smallStyle);
        Fill(new Rect(rect.x + 22f, rect.y + 70f, rect.width - 44f, 1f), dividerColor);
        GUI.Label(new Rect(rect.x + 22f, rect.y + 78f, 290f, 22f), "维修总预算  ¥" + TotalBudget.ToString("N0") + "    结余 ¥" + Remaining.ToString("N0"), bodyStyle);
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
            DrawOrderCard(new Rect(rect.x + 12f, y + i * 52f, rect.width - 24f, 44f), display[i]);
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
            StateText(order) + "　　¥" + order.cost.ToString("N0"), smallStyle);
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
        GUI.Label(new Rect(rect.x + 27f, rect.y + 38f, 220f, 18f), "Q / 滚轮 切换　·　B 收起", smallStyle);

        for (int i = 0; i < tools.Count; i++)
        {
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

            Rect button = new Rect(rect.x + rect.width - 150f, rect.y + 66f, 132f, 34f);
            DrawPanel(button, btnBlue, Color.clear);
            GUI.Label(button, "按 [E] 维修", cardButtonStyle);
            return;
        }

        DrawPanel(rect, new Color(0.04f, 0.06f, 0.08f, 0.6f), Color.clear);
        GUI.Label(new Rect(rect.x + 22f, rect.y + 14f, rect.width - 44f, 24f), "系统自动派单中", bodyStyle);
        GUI.Label(new Rect(rect.x + 22f, rect.y + 38f, rect.width - 44f, 20f),
            CountActive() < MaxActiveOrders ? "下一张工单约 " + Mathf.CeilToInt(orderTimer) + " 秒后到达" : "当前工单已满，先完成现场维修", smallStyle);
    }

    private void DrawHintBar()
    {
        Rect rect = HintRect;
        Fill(rect, new Color(0.03f, 0.05f, 0.07f, 0.9f));
        GUI.Label(rect, "WASD 移动　·　空格 跳跃　·　F 开关门　·　按住 Tab 唤出鼠标　·　Q/滚轮 换工具　·　B 工具包　·　E 维修", centerStyle);
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
        }
    }

    private bool IsPointerOverGui(Vector2 mousePosition)
    {
        Vector2 point = new Vector2(mousePosition.x, Screen.height - mousePosition.y);
        return BudgetRect.Contains(point) || TaskListRect.Contains(point) || MinimapRect.Contains(point)
            || PromptRect.Contains(point) || ToolChipRect.Contains(point) || (bagOpen && BagRect.Contains(point));
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

    // 半透明材质（玻璃）：Standard 着色器切到 Fade 模式
    private Material MakeTransparent(Color color, float alpha, float smoothness)
    {
        Shader shader = Shader.Find("Standard");
        if (shader == null)
        {
            shader = Shader.Find("Legacy Shaders/Transparent/Diffuse");
        }
        Material material = new Material(shader);
        if (material.HasProperty("_Mode"))
        {
            material.SetFloat("_Mode", 3f);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.DisableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = 3000;
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
