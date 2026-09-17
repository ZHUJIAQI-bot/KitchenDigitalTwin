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

    private enum ProblemState
    {
        Pending,    // 待修复（红色感叹号）
        Repairing,  // 修复中（黄色）
        Fixed       // 已修复（绿色）
    }

    private class Problem
    {
        public string code;
        public string title;
        public string room;
        public string cause;
        public string plan;
        public int cost;
        public Vector3 site;
        public ProblemState state;
        public GameObject marker;
        public Renderer[] renderers;
        public float repairProgress;
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
    private readonly List<Problem> points = new List<Problem>();
    private readonly List<Room> rooms = new List<Room>();
    private readonly List<Bounds> obstacles = new List<Bounds>();
    private readonly List<GameObject> generatedObjects = new List<GameObject>();

    private Material[] stateMaterials;
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

    private Vector3 taskCounterPosition = new Vector3(-11f, 0f, 0f);
    private bool taskAccepted;
    private Problem activeProblem;
    private Problem repairingProblem;
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
            BuildProblems();
            BuildPlayer();
        }
        catch (System.Exception e)
        {
            startError = e.GetType().Name + ": " + e.Message;
            Debug.LogError("初始化失败：" + e);
        }

        ShowToast("你是装修公司员工：先去任务台（蓝色柜台）接单，再到业主家上门维修", 8f);
    }

    private void Update()
    {
        HandleCamera();
        HandleClickMove();
        HandleMovement();
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
        Color[] colors = { pendingColor, workingColor, fixedColor };
        stateMaterials = new Material[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            stateMaterials[i] = MakeMaterial(colors[i], 0.05f, 0.35f);
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

        if (input.sqrMagnitude > 0.01f && repairingProblem == null)
        {
            direction = input.normalized;
            moving = true;
            hasMoveTarget = false;
        }
        else if (hasMoveTarget && repairingProblem == null)
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
        if (repairingProblem != null)
        {
            return;
        }
        if (Input.GetMouseButtonDown(0))
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

    // ── 问题点 ────────────────────────────────────────────
    private void BuildProblems()
    {
        AddProblem("P1", "水槽下方渗漏", "厨房", 3800, new Vector3(4f, 0f, 8.5f),
            "水槽柜内给水接口老化渗水", "更换角阀与存水弯，柜底加防水托盘");
        AddProblem("P2", "墙面返潮粉化", "卫生间", 3400, new Vector3(3f, 0f, -7f),
            "外墙渗水导致内墙返潮粉化", "外墙重做防水，内墙铲除后批耐水腻子");
        AddProblem("P3", "地面瓷砖空鼓", "客厅", 2200, new Vector3(21f, 0f, -3f),
            "地面瓷砖局部空鼓松动", "空鼓砖拆除重铺，基层找平");
        AddProblem("P4", "吊灯线路老化", "餐厅", 2600, new Vector3(19f, 0f, 7.5f),
            "吊灯线路绝缘层老化", "更换线路线缆并加装漏电保护");
        AddProblem("P5", "卧室木门变形", "卧室", 1800, new Vector3(15f, 0f, -1f),
            "木门受潮变形开关困难", "调整门铰链并做防潮处理");
    }

    private void AddProblem(string code, string title, string room, int cost, Vector3 site, string cause, string plan)
    {
        Problem problem = new Problem
        {
            code = code,
            title = title,
            room = room,
            cost = cost,
            site = site,
            cause = cause,
            plan = plan,
            state = ProblemState.Pending
        };

        GameObject marker = new GameObject("Marker " + code);
        marker.transform.SetParent(transform, false);
        marker.transform.position = new Vector3(site.x, MarkerHeight, site.z);

        GameObject bar = MakePrimitive(PrimitiveType.Cube, "Bar", marker.transform, new Vector3(0f, 0.34f, 0f), new Vector3(0.09f, 0.3f, 0.09f), Quaternion.identity, stateMaterials[0]);
        GameObject dot = MakePrimitive(PrimitiveType.Sphere, "Dot", marker.transform, new Vector3(0f, 0.06f, 0f), Vector3.one * 0.14f, Quaternion.identity, stateMaterials[0]);
        GameObject ring = CreateCylinder("Ring " + code, new Vector3(site.x, 0.02f, site.z), 0.28f, 0.016f, Quaternion.identity, stateMaterials[0]);
        GameObject beam = CreateCylinder("Beam " + code, new Vector3(site.x, MarkerHeight * 0.5f, site.z), 0.014f, MarkerHeight, Quaternion.identity, stateMaterials[0]);
        ring.transform.SetParent(marker.transform, true);
        beam.transform.SetParent(marker.transform, true);

        problem.marker = marker;
        problem.renderers = new[]
        {
            bar.GetComponent<Renderer>(),
            dot.GetComponent<Renderer>(),
            ring.GetComponent<Renderer>(),
            beam.GetComponent<Renderer>()
        };
        points.Add(problem);
    }

    // ── 交互与任务 ────────────────────────────────────────
    private void DetectInteraction()
    {
        if (repairingProblem != null)
        {
            return;
        }

        // 任务台接单
        if (!taskAccepted && Input.GetKeyDown(KeyCode.E) && Distance2D(playerPosition, taskCounterPosition) < InteractDistance + 0.5f)
        {
            taskAccepted = true;
            ShowToast("接到上门维修单：业主家共 5 处问题，请前往各房间排查修复", 7f);
            return;
        }

        if (!taskAccepted)
        {
            return;
        }

        // 找最近的待修复问题
        Problem nearest = null;
        float best = InteractDistance;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].state != ProblemState.Pending)
            {
                continue;
            }
            float d = Distance2D(playerPosition, points[i].site);
            if (d < best)
            {
                best = d;
                nearest = points[i];
            }
        }
        activeProblem = nearest;

        if (activeProblem != null && Input.GetKeyDown(KeyCode.E))
        {
            StartRepair(activeProblem);
        }
    }

    private void StartRepair(Problem problem)
    {
        repairingProblem = problem;
        problem.state = ProblemState.Repairing;
        problem.repairProgress = 0f;

        Vector3 direction = problem.site - playerPosition;
        direction.y = 0f;
        if (direction.sqrMagnitude > 0.001f)
        {
            player.transform.forward = direction.normalized;
        }

        ShowToast("开始维修 · " + problem.room + " · " + problem.title, 3f);
    }

    private void UpdateRepair()
    {
        if (repairingProblem == null)
        {
            return;
        }

        repairingProblem.repairProgress += Time.deltaTime / RepairDuration;
        if (repairingProblem.repairProgress >= 1f)
        {
            repairingProblem.repairProgress = 1f;
            repairingProblem.state = ProblemState.Fixed;
            spent += repairingProblem.cost;
            ShowToast("维修完成 · " + repairingProblem.room + " · " + repairingProblem.title + "（¥" + repairingProblem.cost.ToString("N0") + "）", 5f);

            repairingProblem = null;
            activeProblem = null;

            if (CountFixed() == points.Count)
            {
                ShowToast("全部维修完成！上门任务结束，改造投入 ¥" + spent.ToString("N0"), 8f);
            }
        }
    }

    private void UpdateMarkers()
    {
        for (int i = 0; i < points.Count; i++)
        {
            Problem problem = points[i];
            if (problem.marker == null)
            {
                continue;
            }

            bool active = taskAccepted;
            problem.marker.SetActive(active);
            if (!active)
            {
                continue;
            }

            Material material = stateMaterials[(int)problem.state];
            for (int r = 0; r < problem.renderers.Length; r++)
            {
                if (problem.renderers[r] != null)
                {
                    problem.renderers[r].sharedMaterial = material;
                }
            }

            float speed = problem.state == ProblemState.Repairing ? 6f : 3f;
            float pulse = 1f + Mathf.Sin(Time.time * speed + i) * 0.07f;
            if (problem == activeProblem || problem == repairingProblem)
            {
                pulse += 0.15f;
            }
            problem.marker.transform.localScale = Vector3.one * pulse;

            Vector3 look = problem.marker.transform.position - viewCamera.transform.position;
            if (look.sqrMagnitude > 0.01f)
            {
                problem.marker.transform.rotation = Quaternion.LookRotation(look, Vector3.up);
            }
        }
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
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].state == ProblemState.Fixed)
            {
                count++;
            }
        }
        return count;
    }

    private void ShowToast(string text, float duration = 4f)
    {
        toastText = text;
        toastTimer = duration;
    }

    // ── 界面 ──────────────────────────────────────────────
    private Rect BudgetRect { get { return new Rect(16f, 16f, 320f, 120f); } }
    private Rect ProgressRect { get { return new Rect(Screen.width - 252f, 16f, 236f, 120f); } }
    private Rect ActionRect { get { return new Rect((Screen.width - 600f) * 0.5f, Screen.height - 180f, 600f, 148f); } }
    private Rect HintRect { get { return new Rect(0f, Screen.height - 30f, Screen.width, 30f); } }

    private void OnGUI()
    {
        EnsureStyles();
        DrawBudgetPanel();
        DrawProgressPanel();
        DrawActionPanel();
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

    private void DrawProgressPanel()
    {
        Rect rect = ProgressRect;
        DrawPanel(rect, panelFill, panelBorder);
        Fill(new Rect(rect.x + 12f, rect.y + 14f, 4f, 44f), fixedColor);
        GUI.Label(new Rect(rect.x + 26f, rect.y + 16f, 190f, 26f), taskAccepted ? "维修进度" : "待接单", titleStyle);
        Fill(new Rect(rect.x + 18f, rect.y + 46f, rect.width - 36f, 1f), dividerColor);
        GUI.Label(new Rect(rect.x + 18f, rect.y + 54f, 200f, 24f), CountFixed() + " / " + points.Count + " 已修复", bodyStyle);

        float ratio = points.Count == 0 ? 0f : (float)CountFixed() / points.Count;
        Fill(new Rect(rect.x + 18f, rect.y + 88f, rect.width - 36f, 6f), new Color(1f, 1f, 1f, 0.1f));
        Fill(new Rect(rect.x + 18f, rect.y + 88f, (rect.width - 36f) * ratio, 6f), fixedColor);
        GUI.Label(new Rect(rect.x + 18f, rect.y + 98f, 200f, 18f), "完成度  " + Mathf.RoundToInt(ratio * 100f) + "%", smallStyle);
    }

    private void DrawActionPanel()
    {
        Rect rect = ActionRect;

        if (repairingProblem != null)
        {
            DrawPanel(rect, panelFill, panelBorder);
            Fill(new Rect(rect.x + 12f, rect.y + 14f, 4f, rect.height - 28f), workingColor);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 14f, rect.width - 56f, 26f), repairingProblem.room + " · " + repairingProblem.title + "（维修中）", titleStyle);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 44f, rect.width - 56f, 22f), "成因：" + repairingProblem.cause, bodyStyle);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 66f, rect.width - 56f, 22f), "方案：" + repairingProblem.plan, bodyStyle);
            Fill(new Rect(rect.x + 28f, rect.y + 96f, rect.width - 56f, 8f), new Color(1f, 1f, 1f, 0.12f));
            Fill(new Rect(rect.x + 28f, rect.y + 96f, (rect.width - 56f) * repairingProblem.repairProgress, 8f), workingColor);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 110f, rect.width - 56f, 22f), "维修进度  " + Mathf.RoundToInt(repairingProblem.repairProgress * 100f) + "%    经费 ¥" + repairingProblem.cost.ToString("N0"), smallStyle);
            return;
        }

        if (!taskAccepted)
        {
            DrawPanel(rect, panelFill, panelBorder);
            Fill(new Rect(rect.x + 12f, rect.y + 14f, 4f, rect.height - 28f), btnBlue);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 16f, rect.width - 56f, 28f), "前往任务台接单", titleStyle);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 50f, rect.width - 56f, 24f), "走到公司里的蓝色柜台旁，按 E 接收业主的上门维修任务", bodyStyle);
            return;
        }

        if (activeProblem != null)
        {
            DrawPanel(rect, panelFill, panelBorder);
            Fill(new Rect(rect.x + 12f, rect.y + 14f, 4f, rect.height - 28f), pendingColor);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 14f, rect.width - 56f, 28f), activeProblem.room + " · " + activeProblem.title, titleStyle);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 46f, rect.width - 56f, 24f), "发现一处问题，按 E 现场维修", bodyStyle);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 74f, rect.width - 56f, 22f), "核定经费 ¥" + activeProblem.cost.ToString("N0"), smallStyle);
            Color prev = GUI.color;
            GUI.color = btnBlue;
            GUI.Label(new Rect(rect.x + 28f, rect.y + 102f, 220f, 32f), "按  [E]  开始维修", buttonStyle);
            GUI.color = prev;
            return;
        }

        DrawPanel(rect, new Color(0.05f, 0.07f, 0.09f, 0.55f), Color.clear);
        GUI.Label(new Rect(rect.x + 24f, rect.y + 14f, rect.width - 48f, 26f), "前往业主家各房间，找到红色感叹号进行维修", centerStyle);
    }

    private void DrawHintBar()
    {
        Rect rect = HintRect;
        Fill(rect, new Color(0.03f, 0.05f, 0.07f, 0.9f));
        GUI.Label(rect, "WASD / 方向键 移动　·　靠近任务台或问题点后按 E　·　镜头固定俯角自动跟随", centerStyle);
    }

    private void DrawToast()
    {
        if (toastTimer <= 0f)
        {
            return;
        }
        float width = Mathf.Min(Screen.width - 80f, 760f);
        Rect rect = new Rect((Screen.width - width) * 0.5f, Screen.height - 350f, width, 46f);
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
        }
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
