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

    // 派单模板：决定某个房间的某个位置会出现什么问题
    private class OrderTemplate
    {
        public string room;
        public string title;
        public string cause;
        public string plan;
        public int costMin;
        public int costMax;
        public Vector3 spot;

        public OrderTemplate(string room, string title, string cause, string plan, int costMin, int costMax, float x, float z)
        {
            this.room = room;
            this.title = title;
            this.cause = cause;
            this.plan = plan;
            this.costMin = costMin;
            this.costMax = costMax;
            spot = new Vector3(x, 0f, z);
        }
    }

    private class Room
    {
        public string name;
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
    private const float WorldMinX = -19f;
    private const float WorldMaxX = 27f;
    private const float WorldMinZ = -9f;
    private const float WorldMaxZ = 11f;

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
        // 草坪顶 -0.02 / 小路顶 0.00 / 路缘石顶 0.06 / 路面顶 -0.06 / 车道线顶 -0.025
        CreateDecoCube("Lawn", new Vector3(4f, -0.27f, 2f), new Vector3(140f, 0.5f, 110f), new Color(0.38f, 0.6f, 0.3f));

        Color asphalt = new Color(0.29f, 0.3f, 0.31f);
        Color pavement = new Color(0.68f, 0.68f, 0.66f);
        CreateDecoCube("Road Main", new Vector3(4f, -0.2f, -15f), new Vector3(140f, 0.28f, 8f), asphalt);
        CreateDecoCube("Curb North", new Vector3(4f, -0.02f, -10.7f), new Vector3(140f, 0.16f, 0.6f), pavement);
        CreateDecoCube("Curb South", new Vector3(4f, -0.02f, -19.3f), new Vector3(140f, 0.16f, 0.6f), pavement);
        for (int x = -60; x < 70; x += 9)
        {
            CreateDecoCube("Road Mark", new Vector3(x, -0.04f, -15f), new Vector3(4f, 0.03f, 0.22f), new Color(0.93f, 0.91f, 0.8f));
        }

        // 通往两栋楼的小路
        CreateDecoCube("Path Company", new Vector3(-13f, -0.06f, -7.6f), new Vector3(3f, 0.12f, 6.4f), pavement);
        CreateDecoCube("Path House", new Vector3(3.4f, -0.06f, -4f), new Vector3(2.8f, 0.12f, 9f), pavement);
        CreateDecoCube("Path House Cross", new Vector3(6f, -0.06f, -9f), new Vector3(8f, 0.12f, 2.4f), pavement);

        // 树木（避开两栋楼的范围）
        float[] tx = { -25f, -21f, 30f, -32f, 34f, 13f, -6f, 26f, 32f, -28f, 8f };
        float[] tz = { -5f, 5f, 3f, 12f, -12f, 15f, 14f, 16f, -6f, -14f, 13f };
        for (int i = 0; i < tx.Length; i++)
        {
            if (!InsideBuilding(tx[i], tz[i]))
            {
                BuildTree(tx[i], tz[i]);
            }
        }

        // 灌木
        for (int i = 0; i < 8; i++)
        {
            float bx = -30f + i * 9f;
            if (InsideBuilding(bx, -9.6f))
            {
                continue;
            }
            CreateDecoSphere("Bush", new Vector3(bx, 0.3f, -9.6f), 0.5f, MakeMaterial(new Color(0.26f, 0.5f, 0.24f), 0.02f, 0.3f));
        }

        // 云
        BuildCloud(new Vector3(-20f, 17f, 26f), 1.2f);
        BuildCloud(new Vector3(12f, 19f, 34f), 1.5f);
        BuildCloud(new Vector3(-2f, 15.5f, 44f), 1.0f);
        BuildCloud(new Vector3(36f, 20f, 18f), 1.3f);
        BuildCloud(new Vector3(-44f, 18f, 10f), 1.4f);
        BuildCloud(new Vector3(24f, 16.5f, 48f), 1.1f);
    }

    // 两栋建筑的外扩范围，用于避免绿化穿模进屋
    private static bool InsideBuilding(float x, float z)
    {
        if (x > -19.8f && x < -6.2f && z > -6.8f && z < 6.8f)
        {
            return true;
        }
        return x > 0.2f && x < 27.8f && z > -9.8f && z < 11.8f;
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
        float xMin = -18f, xMax = -8f, zMin = -5f, zMax = 5f;
        Color wall = new Color(0.87f, 0.85f, 0.8f);

        CreateDecoCube("Company Floor", new Vector3(-13f, 0.01f, 0f), new Vector3(10f, 0.02f, 10f), new Color(0.74f, 0.72f, 0.68f));

        AddSolidBox("Company Wall Back", new Vector3(-13f, WallHeight * 0.5f, zMax), new Vector3(10f, WallHeight, 0.24f), wall);
        AddSolidBox("Company Wall Left", new Vector3(xMin, WallHeight * 0.5f, 0f), new Vector3(0.24f, WallHeight, 10f), wall);
        AddSolidBox("Company Wall Front", new Vector3(-13f, WallHeight * 0.5f, zMin), new Vector3(10f, WallHeight, 0.24f), wall);
        AddWallWithDoorX(xMax, zMin, zMax, WallHeight, 0.24f, -1f, 1f, wall);

        BuildRoof("Company Roof", -13f, 0f, 10f, 10f, new Color(0.42f, 0.36f, 0.34f));

        // 办公区
        BuildDeskStation(-15f, -2f, 180f, false);
        BuildDeskStation(-11f, -2.6f, 180f, true);
        BuildCabinet(-17f, 3.6f, 0f);
        BuildPlant(-17f, -4.2f);
        BuildSofa(-9.9f, 3f, 270f);
        BuildPlant(-9.8f, -4.4f);

        rooms.Add(new Room { name = "装修公司", xMin = xMin, xMax = xMax, zMin = zMin, zMax = zMax });
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

    // ── 住宅 ──────────────────────────────────────────────
    private void BuildHouse()
    {
        float xMin = 2f, xMax = 26f, zMin = -8f, zMax = 10f;

        BuildCheckeredFloor(2f, 12f, 2f, 10f);   // 厨房
        BuildCheckeredFloor(12f, 26f, 2f, 10f);  // 餐厅
        BuildCheckeredFloor(2f, 8f, -8f, 2f);    // 卫生间
        BuildCheckeredFloor(8f, 16f, -8f, 2f);   // 卧室
        BuildCheckeredFloor(16f, 26f, -8f, 2f);  // 客厅

        Color wall = new Color(0.88f, 0.86f, 0.81f);
        Color inner = new Color(0.84f, 0.82f, 0.77f);

        AddSolidBox("House Wall Back", new Vector3(14f, WallHeight * 0.5f, zMax), new Vector3(24f, WallHeight, 0.24f), wall);
        AddSolidBox("House Wall Right", new Vector3(xMax, WallHeight * 0.5f, 1f), new Vector3(0.24f, WallHeight, 18f), wall);
        AddSolidBox("House Wall Front", new Vector3(14f, WallHeight * 0.5f, zMin), new Vector3(24f, WallHeight, 0.24f), wall);
        AddWallWithDoorX(xMin, zMin, zMax, WallHeight, 0.24f, 0f, 2f, wall);

        AddWallWithDoorX(12f, 2f, 10f, WallHeight, 0.22f, 5.2f, 6.8f, inner);   // 厨房|餐厅
        AddHorizontalWallWithDoors(2f, xMin, xMax, WallHeight, 0.22f, inner, 4.5f, 6.5f, 18.5f, 20.5f);
        AddWallWithDoorX(8f, -8f, 2f, WallHeight, 0.22f, -1f, 1f, inner);       // 卫生间|卧室
        AddWallWithDoorX(16f, -8f, 2f, WallHeight, 0.22f, -1f, 1f, inner);      // 卧室|客厅

        BuildRoof("House Roof", 14f, 1f, 24f, 18f, new Color(0.55f, 0.33f, 0.27f));

        BuildKitchenRoom();
        BuildDiningRoom();
        BuildBathroom();
        BuildBedroom();
        BuildLivingRoom();

        rooms.Add(new Room { name = "厨房", xMin = 2f, xMax = 12f, zMin = 2f, zMax = 10f });
        rooms.Add(new Room { name = "餐厅", xMin = 12f, xMax = 26f, zMin = 2f, zMax = 10f });
        rooms.Add(new Room { name = "卫生间", xMin = 2f, xMax = 8f, zMin = -8f, zMax = 2f });
        rooms.Add(new Room { name = "卧室", xMin = 8f, xMax = 16f, zMin = -8f, zMax = 2f });
        rooms.Add(new Room { name = "客厅", xMin = 16f, xMax = 26f, zMin = -8f, zMax = 2f });
    }

    private void BuildKitchenRoom()
    {
        Material cab = MakeMaterial(new Color(0.5f, 0.36f, 0.22f), 0.02f, 0.4f);
        Material door = MakeMaterial(new Color(0.62f, 0.46f, 0.3f), 0.02f, 0.4f);
        Material top = MakeMaterial(new Color(0.74f, 0.73f, 0.7f), 0.05f, 0.5f);
        Material steel = MakeMaterial(new Color(0.72f, 0.75f, 0.78f), 0.75f, 0.7f);

        // 沿后墙的橱柜（z=9）
        for (int x = 3; x <= 11; x += 2)
        {
            DecoPart(PrimitiveType.Cube, "Cabinet", transform, new Vector3(x, 0.45f, 9f), new Vector3(1.9f, 0.9f, 1.1f), Quaternion.identity, cab);
            DecoPart(PrimitiveType.Cube, "Door", transform, new Vector3(x, 0.45f, 8.42f), new Vector3(1.6f, 0.74f, 0.05f), Quaternion.identity, door);
            DecoPart(PrimitiveType.Cylinder, "Handle", transform, new Vector3(x + 0.55f, 0.45f, 8.38f), new Vector3(0.022f, 0.16f, 0.022f), Quaternion.Euler(90f, 0f, 0f), steel);
            // 吊柜
            DecoPart(PrimitiveType.Cube, "Wall Cabinet", transform, new Vector3(x, 1.9f, 9.3f), new Vector3(1.8f, 0.7f, 0.45f), Quaternion.identity, cab);
            DecoPart(PrimitiveType.Cube, "Wall Cabinet Door", transform, new Vector3(x, 1.9f, 9.06f), new Vector3(1.6f, 0.56f, 0.04f), Quaternion.identity, door);
        }
        AddRotatedObstacle("Kitchen Counter", new Vector3(7f, 0.45f, 9f), new Vector3(10f, 0.9f, 1.15f), 0f);
        DecoPart(PrimitiveType.Cube, "Countertop", transform, new Vector3(7f, 0.93f, 9f), new Vector3(10.2f, 0.07f, 1.25f), Quaternion.identity, top);

        // 水槽 + 龙头
        DecoPart(PrimitiveType.Cube, "Sink", transform, new Vector3(4.4f, 0.98f, 9f), new Vector3(1.2f, 0.05f, 0.72f), Quaternion.identity, steel);
        DecoPart(PrimitiveType.Cylinder, "Faucet", transform, new Vector3(4.4f, 1.14f, 9.26f), new Vector3(0.03f, 0.32f, 0.03f), Quaternion.identity, steel);

        // 灶台 + 锅
        DecoPart(PrimitiveType.Cube, "Stove", transform, new Vector3(9f, 0.98f, 9f), new Vector3(1.3f, 0.05f, 0.7f), Quaternion.identity, MakeMaterial(new Color(0.12f, 0.13f, 0.15f), 0.2f, 0.5f));
        DecoPart(PrimitiveType.Cylinder, "Pot", transform, new Vector3(9f, 1.06f, 9f), new Vector3(0.16f, 0.12f, 0.16f), Quaternion.identity, steel);

        // 冰箱
        AddSolidBox("Fridge", new Vector3(11f, 0.9f, 4.6f), new Vector3(0.9f, 1.8f, 0.9f), new Color(0.78f, 0.8f, 0.82f));
        DecoPart(PrimitiveType.Cube, "Fridge Split", transform, new Vector3(11f, 1.15f, 4.14f), new Vector3(0.9f, 0.03f, 0.04f), Quaternion.identity, MakeMaterial(new Color(0.6f, 0.62f, 0.64f), 0.3f, 0.5f));
        DecoPart(PrimitiveType.Cylinder, "Fridge Handle", transform, new Vector3(10.7f, 1.4f, 4.13f), new Vector3(0.02f, 0.3f, 0.02f), Quaternion.identity, steel);

        // 小餐桌 + 两把椅子
        BuildTable(5f, 5f, 1.5f, 1.1f, 0f, 0.72f);
        BuildChair(4.2f, 6.1f, 0f);
        BuildChair(5.8f, 6.1f, 0f);
    }

    private void BuildDiningRoom()
    {
        BuildTable(19f, 6f, 2.4f, 1.3f, 0f, 0.74f);
        BuildChair(17.8f, 6f, 270f);
        BuildChair(20.2f, 6f, 90f);
        BuildChair(19f, 4.9f, 180f);
        BuildChair(19f, 7.1f, 0f);
        BuildCabinet(25f, 8.4f, 90f);
        BuildPlant(13f, 9f);
    }

    private void BuildBathroom()
    {
        BuildToilet(6.4f, -7f, 180f);
        BuildWashbasin(3.2f, -7f, 0f);
        BuildBathtub(5f, -1.8f, 0f);
        BuildPlant(2.9f, -1.6f);
    }

    private void BuildBedroom()
    {
        BuildBed(11.5f, -6f, 180f);
        BuildWardrobe(14.8f, -7f, 0f);
        BuildCabinet(10f, -1.6f, 180f);
        BuildPlant(9f, -7.2f);
    }

    private void BuildLivingRoom()
    {
        BuildSofa(24.3f, -5.5f, 270f);
        BuildTable(21f, -3.5f, 1.6f, 0.9f, 0f, 0.45f);
        BuildTvUnit(18f, -7.2f, 0f);
        CreateDecoCube("Rug", new Vector3(21f, 0.02f, -4.5f), new Vector3(3.6f, 0.02f, 2.6f), new Color(0.68f, 0.55f, 0.44f));
        BuildPlant(25.2f, 0.6f);
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
        // 厨房
        templates.Add(new OrderTemplate("厨房", "水槽下方渗漏", "水槽柜内给水角阀老化，柜底板见渗水痕迹", "更换角阀与存水弯，柜底增设防水托盘", 3200, 4200, 3.6f, 8.6f));
        templates.Add(new OrderTemplate("厨房", "灶台燃气管老化", "燃气软管超期服役，接口处有轻微泄漏", "更换不锈钢波纹管并做气密性检测", 2800, 3800, 7.6f, 8.6f));
        templates.Add(new OrderTemplate("厨房", "橱柜门板变形", "地柜门板受潮变形，开合卡顿异响", "更换门板并调整铰链，柜体做防潮处理", 1200, 2000, 6.5f, 8.0f));
        templates.Add(new OrderTemplate("厨房", "冰箱插座接触不良", "冰箱专用插座松动，插头发热变色", "更换 16A 插座面板并紧固线路", 900, 1600, 10.6f, 5.6f));

        // 卫生间
        templates.Add(new OrderTemplate("卫生间", "地漏返味", "地漏存水弯干涸失效，下水道异味返涌", "更换防臭地漏芯，补做存水弯", 800, 1400, 5f, -5f));
        templates.Add(new OrderTemplate("卫生间", "墙面瓷砖空鼓", "淋浴区瓷砖空鼓脱层，存在脱落风险", "空鼓砖拆除重贴，基层做防水处理", 2200, 3200, 3f, -6.2f));
        templates.Add(new OrderTemplate("卫生间", "马桶底座渗水", "马桶法兰密封圈老化，底座渗水返碱", "更换法兰密封圈并重新打胶固定", 1500, 2400, 6.5f, -6.8f));
        templates.Add(new OrderTemplate("卫生间", "浴缸密封胶老化", "浴缸边缘密封胶发霉开裂，渗水至楼下", "铲除旧胶重新打防霉硅酮胶", 900, 1500, 5f, -1.5f));

        // 卧室
        templates.Add(new OrderTemplate("卧室", "木门变形关不严", "木门受潮膨胀变形，闭合困难漏风", "刨修门边并调整铰链，门扇做防潮封边", 1200, 2000, 14.6f, -1f));
        templates.Add(new OrderTemplate("卧室", "墙面返潮发霉", "外墙渗水导致内墙返潮霉变", "外墙迎水面重做防水，内墙铲除后批耐水腻子", 2800, 3800, 9.2f, -6f));
        templates.Add(new OrderTemplate("卧室", "衣柜滑轨卡顿", "衣柜推拉门滑轨变形积尘，推拉困难", "更换滑轨并调整门扇垂直度", 600, 1200, 14.6f, -6.8f));
        templates.Add(new OrderTemplate("卧室", "床头插座松动", "床头插座面板松动，插拔打火", "更换面板并加固暗盒", 800, 1400, 11f, -5.6f));

        // 客厅
        templates.Add(new OrderTemplate("客厅", "地面瓷砖空鼓", "地面瓷砖局部空鼓脱层，踩踏有松动异响", "空鼓砖拆除重铺，基层找平做界面处理", 1800, 2800, 21f, -3f));
        templates.Add(new OrderTemplate("客厅", "吊顶灯带脱落", "吊顶灯带卡扣老化脱落，线路外露", "更换卡扣并整理线路，加装线槽", 1000, 1800, 20f, -1.2f));
        templates.Add(new OrderTemplate("客厅", "沙发背景墙开裂", "背景墙基层开裂，饰面起皮脱落", "铲除空鼓层，挂网后重新批刮饰面", 2200, 3200, 24.4f, -5.6f));
        templates.Add(new OrderTemplate("客厅", "电视线缆外露", "电视墙线缆杂乱外露，存在安全隐患", "加装线槽归拢线缆并做隐蔽处理", 700, 1300, 17.6f, -6.8f));

        // 餐厅
        templates.Add(new OrderTemplate("餐厅", "吊灯线路老化", "吊灯线路绝缘层老化变脆，有漏电风险", "更换线缆并加装 30mA 漏电保护器", 2000, 3000, 19f, 6f));
        templates.Add(new OrderTemplate("餐厅", "餐边柜受潮", "餐边柜背板受潮发霉，板材膨胀", "更换背板为防潮板并加装离墙通风条", 1600, 2600, 24.4f, 8.4f));
        templates.Add(new OrderTemplate("餐厅", "墙面插座漏电", "墙面插座接线松动，接地不良", "重新接线并测量接地电阻", 1200, 2000, 14f, 6f));

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
        // 只在"当前没有活跃工单"的位置派新单，避免重叠
        List<OrderTemplate> available = new List<OrderTemplate>();
        for (int i = 0; i < templates.Count; i++)
        {
            if (!HasActiveOrderAt(templates[i].spot))
            {
                available.Add(templates[i]);
            }
        }
        if (available.Count == 0)
        {
            return false;
        }

        OrderTemplate template = available[Random.Range(0, available.Count)];
        int cost = Mathf.RoundToInt(Random.Range(template.costMin, template.costMax + 1) / 100f) * 100;

        Order order = new Order
        {
            id = ++orderSerial,
            title = template.title,
            room = template.room,
            cause = template.cause,
            plan = template.plan,
            cost = cost,
            site = template.spot,
            state = OrderState.Pending
        };

        BuildOrderMarker(order);
        orders.Add(order);
        ShowToast("新工单 " + order.Code + " · " + order.room + " " + order.title + "（自动接单）", 4.5f);
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
        GUI.Label(rect, "WASD 移动　·　空格 跳跃　·　鼠标 转视角　·　按住 Tab 唤出鼠标　·　Q/滚轮 换工具　·　B 工具包　·　E 维修", centerStyle);
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
