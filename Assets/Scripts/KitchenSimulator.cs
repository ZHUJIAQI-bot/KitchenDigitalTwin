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
    private Vector3 moveTarget;
    private bool hasMoveTarget;
    private Transform playerBody;
    private Transform leftArmPivot;
    private Transform rightArmPivot;
    private Transform leftLegPivot;
    private Transform rightLegPivot;

    private Camera viewCamera;
    private readonly Vector3 cameraOffset = new Vector3(0f, 10.5f, -7.5f);
    private readonly Vector3 spawnPosition = new Vector3(-13f, 0f, 0f);

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
        }
        catch (System.Exception e)
        {
            startError = e.GetType().Name + ": " + e.Message;
            Debug.LogError("初始化失败：" + e);
        }

        ShowToast("系统自动派单中：新工单会随机出现在业主家各房间，前往现场按 E 维修", 8f);
    }

    private void Update()
    {
        HandleCamera();
        HandleClickMove();
        HandleMovement();
        UpdateOrderSpawning();
        DetectInteraction();
        UpdateRepair();
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
        RenderSettings.ambientLight = new Color(0.5f, 0.5f, 0.48f);
        RenderSettings.ambientIntensity = 0.9f;
        RenderSettings.fog = false;

        GameObject sunObject = new GameObject("Sun");
        Light sun = sunObject.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.0f;
        sun.color = new Color(1f, 0.94f, 0.84f);
        sun.shadows = LightShadows.Soft;
        sunObject.transform.rotation = Quaternion.Euler(52f, -36f, 0f);
        generatedObjects.Add(sunObject);

        // 大地块（小区地面）
        CreateDecoCube("Ground", new Vector3(3f, -0.15f, 0f), new Vector3(60f, 0.3f, 30f), groundColor);

        BuildCompany();
        BuildHouse();
    }

    private void BuildCompany()
    {
        // 装修公司：x [-18,-8], z [-5,5]
        float xMin = -18f, xMax = -8f, zMin = -5f, zMax = 5f;
        CreateDecoCube("Company Floor", new Vector3(-13f, 0.01f, 0f), new Vector3(10f, 0.02f, 10f), new Color(0.7f, 0.75f, 0.78f));

        // 外墙（门在右墙 z=0）
        AddSolidBox("Company Wall Back", new Vector3(-13f, 0.9f, zMax), new Vector3(10f, 1.8f, 0.24f), companyColor);
        AddSolidBox("Company Wall Left", new Vector3(xMin, 0.9f, 0f), new Vector3(0.24f, 1.8f, 10f), companyColor);
        AddSolidBox("Company Wall Front", new Vector3(-13f, 0.9f, zMin), new Vector3(10f, 1.8f, 0.24f), companyColor);
        // 右墙带门洞
        AddWallWithDoorX(-8f, zMin, zMax, 1.8f, 0.24f, -1f, 1f, companyColor);

        // 任务台（蓝色柜台）
        AddSolidBox("Task Counter", new Vector3(-11f, 0.5f, 1.5f), new Vector3(3.2f, 1f, 0.9f), new Color(0.16f, 0.5f, 0.72f));
        CreateDecoCube("Counter Top", new Vector3(-11f, 1.02f, 1.5f), new Vector3(3.4f, 0.08f, 1f), new Color(0.9f, 0.92f, 0.94f));
        // 任务台标识灯（发光小方块，作为"接单点"视觉提示）
        CreateDecoCube("Task Beacon", new Vector3(-11f, 1.3f, 0.6f), new Vector3(0.3f, 0.3f, 0.3f), MakeMaterial(new Color(0.15f, 0.85f, 1f), 0f, 0.5f));

        // ── 办公室家具 ──
        // 两张员工办公桌 + 电脑 + 转椅
        AddSolidBox("Office Desk 1", new Vector3(-15f, 0.45f, -2f), new Vector3(2.4f, 0.9f, 1.1f), woodLightColor);
        CreateDecoCube("Computer 1", new Vector3(-15f, 0.92f, -2.2f), new Vector3(0.7f, 0.45f, 0.12f), new Color(0.1f, 0.12f, 0.15f));
        AddSolidBox("Office Chair 1", new Vector3(-15f, 0.4f, -1.1f), new Vector3(0.6f, 0.8f, 0.6f), new Color(0.3f, 0.35f, 0.4f));

        AddSolidBox("Office Desk 2", new Vector3(-11f, 0.45f, -3f), new Vector3(2.4f, 0.9f, 1.1f), woodLightColor);
        CreateDecoCube("Computer 2", new Vector3(-11f, 0.92f, -3.2f), new Vector3(0.7f, 0.45f, 0.12f), new Color(0.1f, 0.12f, 0.15f));
        AddSolidBox("Office Chair 2", new Vector3(-11f, 0.4f, -2.1f), new Vector3(0.6f, 0.8f, 0.6f), new Color(0.3f, 0.35f, 0.4f));

        // 文件柜（靠后墙）
        AddSolidBox("File Cabinet", new Vector3(-17f, 0.7f, 4f), new Vector3(1.5f, 1.4f, 0.9f), new Color(0.55f, 0.58f, 0.6f));
        CreateDecoCube("File Drawer", new Vector3(-17f, 0.55f, 4.1f), new Vector3(1.3f, 0.5f, 0.06f), new Color(0.7f, 0.72f, 0.74f));

        // 绿植（左前角）
        CreateDecoCube("Plant Pot", new Vector3(-17f, 0.3f, -4.2f), new Vector3(0.5f, 0.6f, 0.5f), new Color(0.6f, 0.4f, 0.3f));
        CreateDecoCube("Plant Leaves", new Vector3(-17f, 0.85f, -4.2f), new Vector3(0.55f, 0.8f, 0.55f), new Color(0.2f, 0.55f, 0.3f));

        // 等候沙发（靠右墙，不挡门）
        AddSolidBox("Waiting Sofa", new Vector3(-9.5f, 0.4f, 3f), new Vector3(2.2f, 0.8f, 0.9f), new Color(0.42f, 0.48f, 0.52f));
        CreateDecoCube("Sofa Back", new Vector3(-9.5f, 0.75f, 3.4f), new Vector3(2.2f, 0.5f, 0.18f), new Color(0.32f, 0.38f, 0.42f));

        rooms.Add(new Room { name = "装修公司", xMin = xMin, xMax = xMax, zMin = zMin, zMax = zMax });
    }

    private void BuildHouse()
    {
        // 住宅：x [2,26], z [-8,10]
        float xMin = 2f, xMax = 26f, zMin = -8f, zMax = 10f;

        // 各房间地板（棋盘格）
        BuildCheckeredFloor(2f, 12f, 2f, 10f);   // 厨房
        BuildCheckeredFloor(12f, 26f, 2f, 10f);  // 餐厅
        BuildCheckeredFloor(2f, 8f, -8f, 2f);    // 卫生间
        BuildCheckeredFloor(8f, 16f, -8f, 2f);   // 卧室
        BuildCheckeredFloor(16f, 26f, -8f, 2f);  // 客厅

        // 外墙（入口门在左墙 z=1）
        AddSolidBox("House Wall Back", new Vector3(14f, 0.9f, zMax), new Vector3(24f, 1.8f, 0.24f), wallColor);
        AddSolidBox("House Wall Right", new Vector3(xMax, 0.9f, 1f), new Vector3(0.24f, 1.8f, 18f), wallColor);
        AddSolidBox("House Wall Front", new Vector3(14f, 0.9f, zMin), new Vector3(24f, 1.8f, 0.24f), wallColor);
        AddWallWithDoorX(xMin, zMin, zMax, 1.8f, 0.24f, 0f, 2f, wallColor);

        // 内墙
        AddWallWithDoorX(12f, 2f, 10f, 1.8f, 0.22f, 5.2f, 6.8f, interiorWallColor);   // 厨房|餐厅
        AddHorizontalWallWithDoors(2f, xMin, xMax, 1.8f, 0.22f, interiorWallColor, 4.5f, 6.5f, 18.5f, 20.5f);
        AddWallWithDoorX(8f, -8f, 2f, 1.8f, 0.22f, -1f, 1f, interiorWallColor);       // 卫生间|卧室
        AddWallWithDoorX(16f, -8f, 2f, 1.8f, 0.22f, -1f, 1f, interiorWallColor);      // 卧室|客厅

        // 房间家具
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
        // 橱柜台面（靠后墙 z≈9）
        AddSolidBox("Kitchen Counter", new Vector3(6.5f, 0.5f, 9f), new Vector3(8f, 1f, 1.2f), woodColor);
        CreateDecoCube("Kitchen Countertop", new Vector3(6.5f, 1.02f, 9f), new Vector3(8.2f, 0.1f, 1.3f), new Color(0.78f, 0.77f, 0.74f));
        // 水槽 + 灶台
        CreateDecoCube("Sink", new Vector3(3.6f, 1.08f, 9f), new Vector3(1.3f, 0.06f, 0.9f), new Color(0.6f, 0.64f, 0.67f));
        CreateDecoCube("Stove", new Vector3(7.6f, 1.08f, 9f), new Vector3(1.4f, 0.06f, 0.85f), new Color(0.1f, 0.12f, 0.13f));
        AddSolidBox("Fridge", new Vector3(11f, 0.9f, 5f), new Vector3(1.1f, 1.8f, 1.3f), new Color(0.72f, 0.75f, 0.77f));
        AddSolidBox("Kitchen Table", new Vector3(5f, 0.45f, 4.5f), new Vector3(2.4f, 0.9f, 1.4f), woodLightColor);
    }

    private void BuildDiningRoom()
    {
        AddSolidBox("Dining Table", new Vector3(19f, 0.45f, 6f), new Vector3(3.4f, 0.9f, 2f), woodLightColor);
        CreateDecoCube("Dining Top", new Vector3(19f, 0.92f, 6f), new Vector3(3.6f, 0.06f, 2.2f), new Color(0.62f, 0.44f, 0.27f));
        // 四把椅子
        float[] cx = { 17.2f, 20.8f, 17.2f, 20.8f };
        float[] cz = { 6f, 6f, 4.8f, 4.8f };
        for (int i = 0; i < 4; i++)
        {
            CreateDecoCube("Chair " + i, new Vector3(cx[i], 0.4f, cz[i]), new Vector3(0.5f, 0.8f, 0.5f), woodColor);
        }
    }

    private void BuildBathroom()
    {
        // 马桶
        AddSolidBox("Toilet", new Vector3(6.5f, 0.45f, -7f), new Vector3(0.8f, 0.9f, 1.1f), new Color(0.85f, 0.88f, 0.9f));
        // 洗手台
        AddSolidBox("Washbasin", new Vector3(3f, 0.5f, -6.5f), new Vector3(1.6f, 1f, 0.9f), new Color(0.8f, 0.84f, 0.87f));
        // 浴缸
        AddSolidBox("Bathtub", new Vector3(5f, 0.35f, -1.5f), new Vector3(4f, 0.7f, 1.6f), new Color(0.78f, 0.82f, 0.85f));
    }

    private void BuildBedroom()
    {
        AddSolidBox("Bed", new Vector3(12f, 0.35f, -6.5f), new Vector3(3.2f, 0.7f, 2.4f), new Color(0.55f, 0.42f, 0.6f));
        CreateDecoCube("Bed Pillow", new Vector3(12f, 0.75f, -5.4f), new Vector3(2.6f, 0.12f, 0.5f), new Color(0.95f, 0.94f, 0.9f));
        AddSolidBox("Wardrobe", new Vector3(15f, 0.9f, -7f), new Vector3(1.8f, 1.8f, 1.2f), woodColor);
    }

    private void BuildLivingRoom()
    {
        AddSolidBox("Sofa", new Vector3(24.5f, 0.4f, -6f), new Vector3(2.2f, 0.8f, 1.6f), new Color(0.4f, 0.5f, 0.55f));
        AddSolidBox("TV Stand", new Vector3(17.5f, 0.4f, -7.2f), new Vector3(3f, 0.8f, 0.8f), woodLightColor);
        AddSolidBox("Coffee Table", new Vector3(21f, 0.35f, -3f), new Vector3(2.4f, 0.7f, 1.3f), woodColor);
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
        if (midH > 0f)
        {
            CreateDecoCube("Lintel", new Vector3(x, height - 0.15f, midZ), new Vector3(thickness, 0.3f, midH), color);
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
        if (midW > 0f)
        {
            CreateDecoCube("Lintel", new Vector3(midX, height - 0.15f, z), new Vector3(midW, 0.3f, thickness), color);
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
            CreateDecoCube("Lintel", new Vector3((doorStart + doorEnd) * 0.5f, height - 0.15f, z), new Vector3(doorEnd - doorStart, 0.3f, thickness), color);
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
        GameObject cameraObject = new GameObject("Follow Camera");
        viewCamera = cameraObject.AddComponent<Camera>();
        cameraObject.AddComponent<AudioListener>();
        cameraObject.tag = "MainCamera";
        viewCamera.fieldOfView = 46f;
        viewCamera.nearClipPlane = 0.1f;
        viewCamera.farClipPlane = 400f;
        viewCamera.clearFlags = CameraClearFlags.SolidColor;
        viewCamera.backgroundColor = new Color(0.09f, 0.12f, 0.14f);
        viewCamera.transform.position = spawnPosition + cameraOffset;
        viewCamera.transform.LookAt(spawnPosition + Vector3.up * 1.1f);
    }

    private void HandleCamera()
    {
        if (player == null || viewCamera == null)
        {
            return;
        }
        Vector3 target = playerPosition + cameraOffset;
        viewCamera.transform.position = Vector3.Lerp(viewCamera.transform.position, target, Time.deltaTime * 7f);
        viewCamera.transform.LookAt(playerPosition + Vector3.up * 1.1f);
    }

    // ── 玩家 ──────────────────────────────────────────────
    private void BuildPlayer()
    {
        player = new GameObject("Inspector");
        playerPosition = spawnPosition;
        player.transform.position = spawnPosition;

        playerBody = MakePrimitive(PrimitiveType.Cube, "Torso", player.transform, new Vector3(0f, 0.85f, 0f), new Vector3(0.5f, 0.7f, 0.3f), Quaternion.identity, MakeMaterial(new Color(0.16f, 0.34f, 0.48f), 0.05f, 0.35f)).transform;
        MakePrimitive(PrimitiveType.Cube, "Head", playerBody, new Vector3(0f, 0.53f, 0f), new Vector3(0.32f, 0.32f, 0.32f), Quaternion.identity, MakeMaterial(new Color(0.82f, 0.64f, 0.47f), 0.02f, 0.3f));
        MakePrimitive(PrimitiveType.Cube, "Helmet", playerBody, new Vector3(0f, 0.72f, 0f), new Vector3(0.42f, 0.1f, 0.42f), Quaternion.identity, MakeMaterial(new Color(0.95f, 0.72f, 0.12f), 0.1f, 0.45f));

        Material bodyMaterial = MakeMaterial(new Color(0.16f, 0.34f, 0.48f), 0.05f, 0.35f);
        Material legMaterial = MakeMaterial(new Color(0.22f, 0.26f, 0.3f), 0.05f, 0.3f);

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
    }

    // ── 移动（手动 AABB 碰撞）────────────────────────────
    private void HandleMovement()
    {
        if (player == null)
        {
            return;
        }

        Vector3 input = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
        input = Vector3.ClampMagnitude(input, 1f);

        Vector3 direction = Vector3.zero;
        bool moving = false;

        if (input.sqrMagnitude > 0.01f && repairingOrder == null)
        {
            direction = input.normalized;
            moving = true;
            hasMoveTarget = false;
        }
        else if (hasMoveTarget && repairingOrder == null)
        {
            Vector3 toTarget = moveTarget - playerPosition;
            toTarget.y = 0f;
            if (toTarget.magnitude > 0.25f)
            {
                direction = toTarget.normalized;
                moving = true;
            }
            else
            {
                hasMoveTarget = false;
            }
        }

        if (!moving)
        {
            AnimateCharacter(false);
            return;
        }

        player.transform.forward = Vector3.Slerp(player.transform.forward, direction, Time.deltaTime * 14f);

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

        player.transform.position = playerPosition;
        AnimateCharacter(true);
    }

    private void HandleClickMove()
    {
        if (viewCamera == null || repairingOrder != null)
        {
            return;
        }
        if (Input.GetMouseButtonDown(0) && !IsPointerOverGui(Input.mousePosition))
        {
            Ray ray = viewCamera.ScreenPointToRay(Input.mousePosition);
            if (ray.direction.y < -0.01f)
            {
                float t = -ray.origin.y / ray.direction.y;
                if (t > 0f)
                {
                    moveTarget = ray.origin + ray.direction * t;
                    moveTarget.y = 0f;
                    hasMoveTarget = true;
                }
            }
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

    private void AnimateCharacter(bool moving)
    {
        if (playerBody == null)
        {
            return;
        }
        float t = Time.time * 9f;
        if (moving)
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
        if (repairingOrder != null)
        {
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
    private Rect HintRect { get { return new Rect(0f, Screen.height - 30f, Screen.width, 30f); } }

    private void OnGUI()
    {
        EnsureStyles();
        DrawBudgetPanel();
        DrawTaskList();
        DrawPromptPanel();
        DrawHintBar();
        DrawToast();
        DrawStartError();
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
        GUI.Label(rect, "WASD / 方向键 移动　·　左键点击地面自动寻路　·　靠近红色感叹号按 E 维修　·　镜头固定俯角跟随", centerStyle);
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
        return BudgetRect.Contains(point) || TaskListRect.Contains(point) || PromptRect.Contains(point);
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

    private GameObject CreateCube(string objectName, Vector3 position, Vector3 scale, Color color)
    {
        return CreateCube(objectName, position, scale, MakeMaterial(color, 0.02f, 0.4f));
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
        return CreateDecoCube(objectName, position, scale, MakeMaterial(color, 0.02f, 0.35f));
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
