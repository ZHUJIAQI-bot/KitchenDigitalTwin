using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 焕新厨房 · 老旧住宅厨房改造数字化仿真原型
/// 玩法：操控一名勘察员小人在厨房里走动（固定俯角镜头跟随），
/// 走到红色感叹号问题点附近按 E 触发并就地修复，控制改造预算。
/// </summary>
public class KitchenSimulator : MonoBehaviour
{
    private const string ProjectName = "焕新厨房";
    private const string ProjectSubtitle = "老旧住宅厨房改造 · 现场勘察修复";
    private const int TotalBudget = 30000;
    private const float MoveSpeed = 3.4f;
    private const float InteractDistance = 1.9f;
    private const float RepairDuration = 2.6f;
    private const float MarkerHeight = 2.5f;

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
        public string category;
        public string cause;
        public string plan;
        public int level;
        public int cost;
        public Vector3 site;
        public ProblemState state;
        public GameObject marker;
        public Renderer[] renderers;
        public float repairProgress;
    }

    // ── 配色 ──────────────────────────────────────────────
    private readonly Color pendingColor = new Color(0.94f, 0.28f, 0.25f);
    private readonly Color workingColor = new Color(1f, 0.76f, 0.18f);
    private readonly Color fixedColor = new Color(0.26f, 0.82f, 0.52f);

    private readonly Color wallColor = new Color(0.76f, 0.78f, 0.76f);
    private readonly Color floorColor = new Color(0.25f, 0.29f, 0.29f);
    private readonly Color woodColor = new Color(0.44f, 0.27f, 0.15f);
    private readonly Color woodAccent = new Color(0.62f, 0.40f, 0.20f);
    private readonly Color stoneColor = new Color(0.67f, 0.69f, 0.67f);
    private readonly Color metalColor = new Color(0.44f, 0.49f, 0.51f);
    private readonly Color copperColor = new Color(0.85f, 0.42f, 0.15f);
    private readonly Color pipeColor = new Color(0.13f, 0.56f, 0.68f);

    private readonly Color bodyColor = new Color(0.16f, 0.34f, 0.48f);
    private readonly Color skinColor = new Color(0.82f, 0.64f, 0.47f);
    private readonly Color helmetColor = new Color(0.95f, 0.72f, 0.12f);
    private readonly Color legColor = new Color(0.22f, 0.26f, 0.3f);

    private readonly Color panelFill = new Color(0.055f, 0.075f, 0.095f, 0.97f);
    private readonly Color panelGlass = new Color(0.07f, 0.09f, 0.11f, 0.88f);
    private readonly Color panelBorder = new Color(1f, 1f, 1f, 0.10f);
    private readonly Color dividerColor = new Color(1f, 1f, 1f, 0.08f);
    private readonly Color btnBlue = new Color(0.18f, 0.47f, 0.63f);
    private readonly Color btnDisabled = new Color(0.20f, 0.23f, 0.27f, 0.95f);
    private readonly Color btnHover = new Color(1f, 1f, 1f, 0.12f);

    // ── 运行时状态 ────────────────────────────────────────
    private readonly List<Problem> points = new List<Problem>();
    private readonly List<GameObject> generatedObjects = new List<GameObject>();

    private Material[] stateMaterials;
    private Color[] stateColors;
    private Material bodyMaterial;
    private Material skinMaterial;
    private Material helmetMaterial;
    private Material legMaterial;
    private Material metalMaterial;
    private Material brassMaterial;
    private Material stoneMaterial;
    private Material woodMaterial;
    private Material woodLightMaterial;
    private Material glassMaterial;

    private GameObject player;
    private CharacterController controller;
    private Transform playerBody;
    private Transform leftArmPivot;
    private Transform rightArmPivot;
    private Transform leftLegPivot;
    private Transform rightLegPivot;
    private Vector3 playerVelocity;

    private Camera viewCamera;
    private readonly Vector3 cameraOffset = new Vector3(0f, 9.5f, -7f);
    private readonly Vector3 spawnPosition = new Vector3(0f, 0.1f, -3.4f);

    private Problem activeProblem;
    private Problem repairingProblem;
    private int spent;
    private string toastText = string.Empty;
    private float toastTimer;

    private GUIStyle titleStyle;
    private GUIStyle hintStyle;
    private GUIStyle bodyStyle;
    private GUIStyle smallStyle;
    private GUIStyle buttonStyle;
    private GUIStyle toastStyle;
    private GUIStyle centerStyle;

    private int Remaining { get { return TotalBudget - spent; } }

    // ── 生命周期 ──────────────────────────────────────────
    private void Start()
    {
        Application.targetFrameRate = 60;
        Time.maximumDeltaTime = 0.1f; // 防止 WebGL 首帧大步长导致穿模
        BuildMaterials();
        BuildEnvironment();
        BuildCamera();
        BuildProblemPoints();
        BuildCharacter();
        ShowToast("操作勘察员前往红色感叹号处，靠近后按 E 检查并修复问题", 7f);
    }

    private void Update()
    {
        HandleCamera();
        HandleMovement();
        DetectInteraction();
        UpdateRepair();
        UpdateMarkers();
        if (toastTimer > 0f)
        {
            toastTimer -= Time.deltaTime;
        }
    }

    // ── 资源准备 ──────────────────────────────────────────
    private void BuildMaterials()
    {
        stateColors = new[] { pendingColor, workingColor, fixedColor };
        stateMaterials = new Material[stateColors.Length];
        for (int i = 0; i < stateColors.Length; i++)
        {
            stateMaterials[i] = MakeMaterial(stateColors[i], 0.05f, 0.3f, true);
        }

        bodyMaterial = MakeMaterial(bodyColor, 0.05f, 0.35f);
        skinMaterial = MakeMaterial(skinColor, 0.02f, 0.3f);
        helmetMaterial = MakeMaterial(helmetColor, 0.1f, 0.45f);
        legMaterial = MakeMaterial(legColor, 0.05f, 0.3f);

        metalMaterial = MakeMaterial(new Color(0.55f, 0.58f, 0.62f), 0.8f, 0.75f);
        brassMaterial = MakeMaterial(new Color(0.72f, 0.53f, 0.30f), 0.7f, 0.62f);
        stoneMaterial = MakeMaterial(new Color(0.79f, 0.78f, 0.75f), 0.03f, 0.22f);
        woodMaterial = MakeMaterial(new Color(0.46f, 0.29f, 0.15f), 0.04f, 0.42f);
        woodLightMaterial = MakeMaterial(new Color(0.60f, 0.42f, 0.25f), 0.04f, 0.42f);
        glassMaterial = MakeTransparent(new Color(0.70f, 0.85f, 0.95f), 0.32f, 0.95f);
    }

    private void BuildEnvironment()
    {
        // ── 光照（单一暖主光 + 一盏吊灯，避免过曝）─────
        RenderSettings.ambientLight = new Color(0.44f, 0.44f, 0.42f);
        RenderSettings.ambientIntensity = 0.8f;
        RenderSettings.fog = false;

        GameObject sunObject = new GameObject("Kitchen Sun");
        Light sun = sunObject.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.0f;
        sun.color = new Color(1f, 0.94f, 0.84f);
        sun.shadows = LightShadows.Soft;
        sunObject.transform.rotation = Quaternion.Euler(50f, -40f, 0f);
        generatedObjects.Add(sunObject);

        GameObject ceilingLightObject = new GameObject("Ceiling Light");
        Light ceilingLight = ceilingLightObject.AddComponent<Light>();
        ceilingLight.type = LightType.Point;
        ceilingLight.range = 12f;
        ceilingLight.intensity = 1.8f;
        ceilingLight.color = new Color(1f, 0.9f, 0.75f);
        ceilingLightObject.transform.position = new Vector3(0f, 2.5f, -0.5f);
        generatedObjects.Add(ceilingLightObject);

        // ── 地面 ──────────────────────────────────────
        CreateCube("Floor", new Vector3(0f, -0.15f, 0f), new Vector3(10f, 0.3f, 8f), new Color(0.30f, 0.28f, 0.26f));
        BuildTileFloor();

        // ── 墙体 ──────────────────────────────────────
        CreateCube("Back Wall", new Vector3(0f, 1.25f, 3.9f), new Vector3(10f, 2.7f, 0.25f), wallColor);
        CreateCube("Left Wall", new Vector3(-4.9f, 1.25f, 0f), new Vector3(0.25f, 2.7f, 8f), wallColor);
        CreateCube("Right Wall", new Vector3(4.9f, 1.25f, 0f), new Vector3(0.25f, 2.7f, 8f), wallColor);
        CreateCube("Front Sill", new Vector3(0f, 0.2f, -4.0f), new Vector3(10f, 0.6f, 0.22f), wallColor);

        // ── 装饰与家具 ────────────────────────────────
        BuildBaseboards();
        BuildWindow();
        BuildCabinets();
        BuildCountertop();
        BuildSink();
        BuildStove();
        BuildFridge();
        BuildPipes();
        BuildRug();
        BuildCeilingFixture();
    }

    private void BuildTileFloor()
    {
        Color tileA = new Color(0.88f, 0.81f, 0.69f);
        Color tileB = new Color(0.58f, 0.52f, 0.43f);
        for (int ix = -5; ix < 5; ix++)
        {
            for (int iz = -4; iz < 4; iz++)
            {
                bool light = ((ix + iz) & 1) == 0;
                CreateDecoCube("Tile", new Vector3(ix + 0.5f, 0.006f, iz + 0.5f), new Vector3(0.97f, 0.012f, 0.97f), light ? tileA : tileB);
            }
        }
    }

    private void BuildBaseboards()
    {
        Color trim = new Color(0.5f, 0.42f, 0.32f);
        CreateDecoCube("Baseboard Back", new Vector3(0f, 0.09f, 3.77f), new Vector3(9.5f, 0.18f, 0.03f), trim);
        CreateDecoCube("Baseboard Left", new Vector3(-4.77f, 0.09f, 0f), new Vector3(0.03f, 0.18f, 7.5f), trim);
        CreateDecoCube("Baseboard Right", new Vector3(4.77f, 0.09f, 0f), new Vector3(0.03f, 0.18f, 7.5f), trim);
    }

    private void BuildWindow()
    {
        Vector3 center = new Vector3(4.76f, 1.5f, 2.2f);
        Color frameColor = new Color(0.5f, 0.46f, 0.42f);
        CreateDecoCube("Window Frame", center, new Vector3(0.14f, 1.6f, 1.7f), frameColor);
        CreateDecoCube("Window Glass", new Vector3(4.77f, 1.5f, 2.2f), new Vector3(0.03f, 1.42f, 1.52f), glassMaterial);
        CreateDecoCube("Window Mullion H", new Vector3(4.78f, 1.5f, 2.2f), new Vector3(0.05f, 0.06f, 1.6f), frameColor);
        CreateDecoCube("Window Mullion V", new Vector3(4.78f, 1.5f, 2.2f), new Vector3(0.05f, 1.5f, 0.06f), frameColor);
        CreateDecoCube("Window Sill", new Vector3(4.7f, 0.72f, 2.2f), new Vector3(0.35f, 0.06f, 1.75f), new Color(0.62f, 0.56f, 0.5f));
    }

    private void BuildCabinets()
    {
        // 地柜：深木柜体 + 浅色柜门 + 黄铜拉手
        for (int x = -4; x <= 4; x += 2)
        {
            CreateCube("Base Cabinet " + x, new Vector3(x, 0.82f, 3.2f), new Vector3(1.85f, 1.65f, 1.2f), woodMaterial);
            CreateDecoCube("Door " + x, new Vector3(x, 0.82f, 2.56f), new Vector3(1.5f, 1.22f, 0.06f), woodLightMaterial);
            CreateDecoCube("Door Panel " + x, new Vector3(x, 0.82f, 2.52f), new Vector3(1.08f, 0.84f, 0.03f), woodMaterial);
            CreateDecoCylinder("Handle " + x, new Vector3(x + 0.26f, 0.85f, 2.5f), 0.035f, 0.42f, Quaternion.Euler(90f, 0f, 0f), brassMaterial);
        }

        // 吊柜：浅木柜体 + 深色柜门 + 拉手
        float[] upperX = { -3.15f, -0.85f, 1.45f };
        for (int i = 0; i < upperX.Length; i++)
        {
            float x = upperX[i];
            CreateDecoCube("Upper Cabinet " + i, new Vector3(x, 2.3f, 3.42f), new Vector3(2.05f, 0.65f, 0.65f), woodLightMaterial);
            CreateDecoCube("Upper Door " + i, new Vector3(x, 2.3f, 3.09f), new Vector3(1.9f, 0.55f, 0.05f), woodMaterial);
            CreateDecoCube("Upper Panel " + i, new Vector3(x, 2.3f, 3.06f), new Vector3(1.5f, 0.36f, 0.03f), woodLightMaterial);
            CreateDecoCylinder("Upper Handle " + i, new Vector3(x, 2.26f, 3.05f), 0.03f, 0.3f, Quaternion.identity, brassMaterial);
        }
    }

    private void BuildCountertop()
    {
        CreateCube("Countertop", new Vector3(0f, 1.72f, 3.18f), new Vector3(9.65f, 0.18f, 1.27f), stoneMaterial);
        CreateDecoCube("Countertop Edge", new Vector3(0f, 1.66f, 2.55f), new Vector3(9.65f, 0.06f, 0.04f), stoneMaterial);

        // 挡水板：马赛克瓷砖（白 / 浅青绿相间）
        Color mosaicA = new Color(0.92f, 0.91f, 0.87f);
        Color mosaicB = new Color(0.58f, 0.71f, 0.67f);
        for (int ix = 0; ix < 10; ix++)
        {
            float x = -4.5f + (ix + 0.5f);
            bool light = (ix & 1) == 0;
            CreateDecoCube("Mosaic", new Vector3(x, 1.88f, 3.75f), new Vector3(0.9f, 0.24f, 0.02f), light ? mosaicA : mosaicB);
        }
    }

    private void BuildSink()
    {
        CreateCube("Sink Basin", new Vector3(-2.2f, 1.84f, 3.14f), new Vector3(1.4f, 0.12f, 0.8f), metalMaterial);
        CreateDecoCylinder("Sink Drain", new Vector3(-2.2f, 1.85f, 3.14f), 0.06f, 0.02f, Quaternion.identity, new Color(0.3f, 0.33f, 0.35f));
        // 鹅颈水龙头
        CreateDecoCylinder("Faucet Base", new Vector3(-2.2f, 1.94f, 3.3f), 0.05f, 0.14f, Quaternion.identity, brassMaterial);
        CreateDecoCylinder("Faucet Neck", new Vector3(-2.2f, 2.08f, 3.22f), 0.03f, 0.34f, Quaternion.Euler(90f, 0f, 0f), brassMaterial);
        CreateDecoCylinder("Faucet Spout", new Vector3(-2.2f, 2.0f, 3.06f), 0.026f, 0.2f, Quaternion.identity, brassMaterial);
    }

    private void BuildStove()
    {
        CreateCube("Cooktop", new Vector3(2.15f, 1.84f, 3.14f), new Vector3(1.55f, 0.12f, 0.82f), new Color(0.09f, 0.11f, 0.12f));
        // 炉头
        float[] bx = { 1.85f, 2.45f, 1.85f, 2.45f };
        float[] bz = { 3.3f, 3.3f, 3.0f, 3.0f };
        for (int i = 0; i < 4; i++)
        {
            CreateDecoCylinder("Burner " + i, new Vector3(bx[i], 1.9f, bz[i]), 0.11f, 0.03f, Quaternion.identity, metalMaterial);
            CreateDecoCylinder("Burner Ring " + i, new Vector3(bx[i], 1.9f, bz[i]), 0.05f, 0.02f, Quaternion.identity, new Color(0.2f, 0.22f, 0.24f));
        }
        // 汤锅 + 锅盖
        CreateDecoCylinder("Pot", new Vector3(2.15f, 1.97f, 3.0f), 0.16f, 0.14f, Quaternion.identity, new Color(0.25f, 0.28f, 0.3f));
        CreateDecoCylinder("Pot Lid", new Vector3(2.15f, 2.05f, 3.0f), 0.15f, 0.05f, Quaternion.identity, metalMaterial);
        CreateDecoCube("Pot Handle", new Vector3(2.47f, 1.97f, 3.0f), new Vector3(0.2f, 0.02f, 0.03f), new Color(0.2f, 0.22f, 0.24f));
        // 油烟机
        CreateCube("Range Hood", new Vector3(2.15f, 2.15f, 2.75f), new Vector3(1.75f, 0.34f, 0.62f), metalMaterial);
        CreateDecoCube("Hood Duct", new Vector3(2.15f, 2.4f, 2.75f), new Vector3(0.5f, 0.4f, 0.5f), new Color(0.3f, 0.33f, 0.35f));
    }

    private void BuildFridge()
    {
        CreateCube("Fridge", new Vector3(4f, 1.7f, 0.7f), new Vector3(1.32f, 3.15f, 1.55f), new Color(0.72f, 0.75f, 0.77f));
        CreateDecoCube("Fridge Split", new Vector3(4f, 1.7f, 0.68f), new Vector3(1.33f, 0.02f, 1.52f), new Color(0.55f, 0.58f, 0.6f));
        CreateDecoCube("Fridge Handle Top", new Vector3(3.42f, 2.2f, 0.66f), new Vector3(0.05f, 1.1f, 0.06f), metalMaterial);
        CreateDecoCube("Fridge Handle Bottom", new Vector3(3.42f, 0.9f, 0.66f), new Vector3(0.05f, 0.9f, 0.06f), metalMaterial);
    }

    private void BuildPipes()
    {
        CreateDecoCylinder("Water Pipe", new Vector3(-2.2f, 2.1f, 3.6f), 0.055f, 0.6f, Quaternion.Euler(90f, 0f, 0f), new Color(0.35f, 0.42f, 0.45f));
        CreateDecoCylinder("Gas Pipe", new Vector3(2.15f, 2.1f, 3.6f), 0.055f, 0.6f, Quaternion.Euler(90f, 0f, 0f), brassMaterial);
        CreateDecoCube("Gas Valve", new Vector3(2.15f, 2.18f, 3.6f), new Vector3(0.12f, 0.16f, 0.12f), brassMaterial);
    }

    private void BuildRug()
    {
        CreateDecoCube("Rug", new Vector3(0f, 0.015f, -0.3f), new Vector3(3.4f, 0.02f, 2.2f), new Color(0.62f, 0.38f, 0.28f));
        CreateDecoCube("Rug Border", new Vector3(0f, 0.018f, -0.3f), new Vector3(3.1f, 0.02f, 1.9f), new Color(0.74f, 0.52f, 0.38f));
        CreateDecoCube("Rug Center", new Vector3(0f, 0.021f, -0.3f), new Vector3(2.7f, 0.02f, 1.5f), new Color(0.86f, 0.68f, 0.50f));
    }

    private void BuildCeilingFixture()
    {
        CreateDecoCylinder("Ceiling Lamp", new Vector3(0f, 2.52f, -0.5f), 0.4f, 0.08f, Quaternion.identity, new Color(0.95f, 0.9f, 0.78f));
        CreateDecoCube("Ceiling Glow", new Vector3(0f, 2.48f, -0.5f), new Vector3(0.5f, 0.06f, 0.5f), MakeMaterial(new Color(0.98f, 0.93f, 0.8f), 0f, 0.5f));
    }

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

    private void BuildProblemPoints()
    {
        AddProblem("P1", "水槽下方渗漏", "给排水", 3, 3800, new Vector3(-2.2f, 0f, 2.45f),
            "水槽柜内给水角阀与排水管接口老化，柜底板可见渗水痕迹",
            "更换角阀与存水弯，柜底增设防水托盘及防潮垫层");

        AddProblem("P2", "插座距水源过近", "用电安全", 3, 2600, new Vector3(-0.55f, 0f, 2.75f),
            "台面插座距水槽边不足 0.6m，且该回路未设置漏电保护",
            "插座移位至水槽侧 0.9m 以外，回路加装 30mA 漏电保护器");

        AddProblem("P3", "燃气管与吊柜冲突", "燃气安全", 3, 4600, new Vector3(2.15f, 0f, 2.9f),
            "燃气立管穿越吊柜柜体，柜门遮挡管线检修口",
            "改移燃气管线路由，吊柜局部断开并预留可开启检修口");

        AddProblem("P4", "地柜板材受潮", "柜体受潮", 2, 5200, new Vector3(-3.35f, 0f, 2.35f),
            "水槽相邻地柜受潮膨胀，封边开裂，板材含水率超标",
            "受潮柜体更换为防潮多层板，全柜重新封边并加装柜底防水膜");

        AddProblem("P5", "墙面返潮粉化", "墙面基层", 2, 3400, new Vector3(-4.45f, 0f, 0.7f),
            "外墙渗水导致内墙饰面返潮粉化，基层强度不足",
            "外墙迎水面重做防水层，内墙铲除粉化层后批刮耐水腻子");

        AddProblem("P6", "地面瓷砖空鼓", "地面工程", 1, 2200, new Vector3(1.3f, 0f, -1.7f),
            "地面瓷砖局部空鼓脱层，踩踏存在明显松动异响",
            "空鼓砖拆除重铺，基层找平并做界面剂处理");
    }

    private void AddProblem(string code, string title, string category, int level, int cost, Vector3 site, string cause, string plan)
    {
        Problem problem = new Problem
        {
            code = code,
            title = title,
            category = category,
            level = level,
            cost = cost,
            site = site,
            cause = cause,
            plan = plan,
            state = ProblemState.Pending
        };

        // 头顶感叹号
        GameObject marker = new GameObject("Marker " + code);
        marker.transform.SetParent(transform, false);
        marker.transform.position = new Vector3(site.x, MarkerHeight, site.z);

        GameObject bar = MakePrimitive(PrimitiveType.Cube, "Bar", marker.transform, new Vector3(0f, 0.46f, 0f), new Vector3(0.11f, 0.4f, 0.11f), Quaternion.identity, stateMaterials[0]);
        GameObject dot = MakePrimitive(PrimitiveType.Sphere, "Dot", marker.transform, new Vector3(0f, 0.08f, 0f), Vector3.one * 0.2f, Quaternion.identity, stateMaterials[0]);

        // 地面定位环 + 垂直引线
        GameObject ring = CreateCylinder("Ring " + code, new Vector3(site.x, 0.02f, site.z), 0.34f, 0.02f, Quaternion.identity, stateMaterials[0]);
        GameObject beam = CreateCylinder("Beam " + code, new Vector3(site.x, MarkerHeight * 0.5f, site.z), 0.016f, MarkerHeight, Quaternion.identity, stateMaterials[0]);

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

    private void BuildCharacter()
    {
        player = new GameObject("Inspector");
        controller = player.AddComponent<CharacterController>();
        controller.height = 1.6f;
        controller.radius = 0.35f;
        controller.center = new Vector3(0f, 0.8f, 0f);
        controller.stepOffset = 0.3f;
        player.transform.position = spawnPosition;

        // 躯干 + 头（整体参与行走起伏）
        playerBody = MakePrimitive(PrimitiveType.Cube, "Torso", player.transform, new Vector3(0f, 0.85f, 0f), new Vector3(0.5f, 0.7f, 0.3f), Quaternion.identity, bodyMaterial).transform;
        MakePrimitive(PrimitiveType.Cube, "Head", playerBody, new Vector3(0f, 0.53f, 0f), new Vector3(0.32f, 0.32f, 0.32f), Quaternion.identity, skinMaterial);
        MakePrimitive(PrimitiveType.Cube, "Helmet", playerBody, new Vector3(0f, 0.72f, 0f), new Vector3(0.42f, 0.1f, 0.42f), Quaternion.identity, helmetMaterial);

        // 手臂（肩部枢轴）
        leftArmPivot = new GameObject("Left Arm Pivot").transform;
        leftArmPivot.SetParent(player.transform, false);
        leftArmPivot.localPosition = new Vector3(-0.32f, 1.1f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Left Arm", leftArmPivot, new Vector3(0f, -0.28f, 0f), new Vector3(0.14f, 0.56f, 0.14f), Quaternion.identity, bodyMaterial);
        rightArmPivot = new GameObject("Right Arm Pivot").transform;
        rightArmPivot.SetParent(player.transform, false);
        rightArmPivot.localPosition = new Vector3(0.32f, 1.1f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Right Arm", rightArmPivot, new Vector3(0f, -0.28f, 0f), new Vector3(0.14f, 0.56f, 0.14f), Quaternion.identity, bodyMaterial);

        // 腿（髋部枢轴）
        leftLegPivot = new GameObject("Left Leg Pivot").transform;
        leftLegPivot.SetParent(player.transform, false);
        leftLegPivot.localPosition = new Vector3(-0.13f, 0.68f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Left Leg", leftLegPivot, new Vector3(0f, -0.28f, 0f), new Vector3(0.16f, 0.56f, 0.16f), Quaternion.identity, legMaterial);
        rightLegPivot = new GameObject("Right Leg Pivot").transform;
        rightLegPivot.SetParent(player.transform, false);
        rightLegPivot.localPosition = new Vector3(0.13f, 0.68f, 0f);
        MakePrimitive(PrimitiveType.Cube, "Right Leg", rightLegPivot, new Vector3(0f, -0.28f, 0f), new Vector3(0.16f, 0.56f, 0.16f), Quaternion.identity, legMaterial);
    }

    // ── 相机：固定俯角跟随 ────────────────────────────────
    private void HandleCamera()
    {
        if (player == null || viewCamera == null)
        {
            return;
        }

        Vector3 target = player.transform.position + cameraOffset;
        viewCamera.transform.position = Vector3.Lerp(viewCamera.transform.position, target, Time.deltaTime * 7f);
        viewCamera.transform.LookAt(player.transform.position + Vector3.up * 1.1f);
    }

    // ── 移动 ──────────────────────────────────────────────
    private void HandleMovement()
    {
        if (player == null || controller == null)
        {
            return;
        }

        Vector3 input = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
        input = Vector3.ClampMagnitude(input, 1f);

        bool moving = input.sqrMagnitude > 0.01f && repairingProblem == null;
        Vector3 motion = Vector3.zero;

        if (moving)
        {
            Vector3 direction = input;
            direction.Normalize();
            player.transform.forward = Vector3.Slerp(player.transform.forward, direction, Time.deltaTime * 14f);
            motion = direction * MoveSpeed;
        }

        playerVelocity.y += Physics.gravity.y * Time.deltaTime;
        if (controller.isGrounded && playerVelocity.y < 0f)
        {
            playerVelocity.y = -1f;
        }
        motion.y = playerVelocity.y;
        controller.Move(motion * Time.deltaTime);

        player.transform.position = new Vector3(
            Mathf.Clamp(player.transform.position.x, -4.25f, 4.25f),
            player.transform.position.y,
            Mathf.Clamp(player.transform.position.z, -3.5f, 3.2f));

        // 安全钳制：防止穿模后无限下坠（地板顶面在 y=0）
        if (player.transform.position.y < 0f)
        {
            Vector3 pos = player.transform.position;
            pos.y = 0f;
            player.transform.position = pos;
            if (playerVelocity.y < 0f)
            {
                playerVelocity.y = 0f;
            }
        }

        AnimateCharacter(moving);
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

    // ── 触发与修复 ────────────────────────────────────────
    private void DetectInteraction()
    {
        if (repairingProblem != null)
        {
            return;
        }

        Problem nearest = null;
        float best = InteractDistance;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].state != ProblemState.Pending)
            {
                continue;
            }
            Vector3 offset = points[i].site - player.transform.position;
            offset.y = 0f;
            if (offset.magnitude < best)
            {
                best = offset.magnitude;
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

        Vector3 direction = problem.site - player.transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude > 0.001f)
        {
            player.transform.forward = direction.normalized;
        }

        ShowToast("开始修复 · " + problem.code + " " + problem.title, 3f);
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
            ShowToast("修复完成 · " + repairingProblem.code + " " + repairingProblem.title + "（经费 ¥" + repairingProblem.cost.ToString("N0") + "）", 5f);

            repairingProblem = null;
            activeProblem = null;

            if (CountFixed() == points.Count)
            {
                ShowToast("全部问题修复完成！改造投入合计 ¥" + spent.ToString("N0"), 8f);
            }
        }
    }

    // ── 标记刷新 ──────────────────────────────────────────
    private void UpdateMarkers()
    {
        for (int i = 0; i < points.Count; i++)
        {
            Problem problem = points[i];
            if (problem.marker == null)
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
            float pulse = 1f + Mathf.Sin(Time.time * speed + i) * 0.06f;
            if (problem == activeProblem || problem == repairingProblem)
            {
                pulse += 0.16f;
            }
            problem.marker.transform.localScale = Vector3.one * pulse;

            // 感叹号始终面向镜头
            Vector3 look = problem.marker.transform.position - viewCamera.transform.position;
            if (look.sqrMagnitude > 0.01f)
            {
                problem.marker.transform.rotation = Quaternion.LookRotation(look, Vector3.up);
            }
        }
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
    private Rect BudgetRect { get { return new Rect(16f, 16f, 340f, 128f); } }
    private Rect ProgressRect { get { return new Rect(Screen.width - 252f, 16f, 236f, 128f); } }
    private Rect ActionRect { get { return new Rect((Screen.width - 600f) * 0.5f, Screen.height - 190f, 600f, 158f); } }
    private Rect HintRect { get { return new Rect(0f, Screen.height - 30f, Screen.width, 30f); } }

    private void OnGUI()
    {
        EnsureStyles();
        DrawBudgetPanel();
        DrawProgressPanel();
        DrawActionPanel();
        DrawHintBar();
        DrawToast();
    }

    private void DrawBudgetPanel()
    {
        Rect rect = BudgetRect;
        DrawPanel(rect, panelFill, panelBorder);
        Fill(new Rect(rect.x + 12f, rect.y + 14f, 4f, 46f), pendingColor);
        GUI.Label(new Rect(rect.x + 26f, rect.y + 16f, 300f, 30f), ProjectName, titleStyle);
        GUI.Label(new Rect(rect.x + 27f, rect.y + 48f, 310f, 20f), ProjectSubtitle, smallStyle);
        Fill(new Rect(rect.x + 22f, rect.y + 74f, rect.width - 44f, 1f), dividerColor);

        GUI.Label(new Rect(rect.x + 22f, rect.y + 82f, 320f, 22f), "改造总预算  ¥" + TotalBudget.ToString("N0"), bodyStyle);
        Color previous = GUI.color;
        GUI.color = Remaining < 5000 ? new Color(1f, 0.62f, 0.28f) : new Color(0.55f, 0.9f, 0.75f);
        GUI.Label(new Rect(rect.x + 22f, rect.y + 104f, 320f, 22f), "已支出 ¥" + spent.ToString("N0") + "    结余 ¥" + Remaining.ToString("N0"), bodyStyle);
        GUI.color = previous;
    }

    private void DrawProgressPanel()
    {
        Rect rect = ProgressRect;
        DrawPanel(rect, panelFill, panelBorder);
        Fill(new Rect(rect.x + 12f, rect.y + 14f, 4f, 46f), fixedColor);
        GUI.Label(new Rect(rect.x + 26f, rect.y + 16f, 200f, 28f), "修复进度", titleStyle);
        Fill(new Rect(rect.x + 18f, rect.y + 48f, rect.width - 36f, 1f), dividerColor);

        GUI.Label(new Rect(rect.x + 18f, rect.y + 58f, 210f, 26f), CountFixed() + " / " + points.Count + " 已修复", bodyStyle);

        float ratio = points.Count == 0 ? 0f : (float)CountFixed() / points.Count;
        Fill(new Rect(rect.x + 18f, rect.y + 94f, rect.width - 36f, 6f), new Color(1f, 1f, 1f, 0.1f));
        Fill(new Rect(rect.x + 18f, rect.y + 94f, (rect.width - 36f) * ratio, 6f), fixedColor);
        GUI.Label(new Rect(rect.x + 18f, rect.y + 104f, 210f, 18f), "完成度  " + Mathf.RoundToInt(ratio * 100f) + "%", smallStyle);
    }

    private void DrawActionPanel()
    {
        Rect rect = ActionRect;

        if (repairingProblem != null)
        {
            DrawPanel(rect, panelFill, panelBorder);
            Fill(new Rect(rect.x + 12f, rect.y + 14f, 4f, rect.height - 28f), workingColor);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 14f, rect.width - 56f, 28f), repairingProblem.code + " · " + repairingProblem.title + "（修复中）", titleStyle);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 46f, rect.width - 56f, 22f), "成因：" + repairingProblem.cause, bodyStyle);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 70f, rect.width - 56f, 22f), "方案：" + repairingProblem.plan, bodyStyle);

            Fill(new Rect(rect.x + 28f, rect.y + 100f, rect.width - 56f, 8f), new Color(1f, 1f, 1f, 0.12f));
            Fill(new Rect(rect.x + 28f, rect.y + 100f, (rect.width - 56f) * repairingProblem.repairProgress, 8f), workingColor);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 114f, rect.width - 56f, 24f), "修复进度  " + Mathf.RoundToInt(repairingProblem.repairProgress * 100f) + "%    经费 ¥" + repairingProblem.cost.ToString("N0"), smallStyle);
            return;
        }

        if (activeProblem != null)
        {
            DrawPanel(rect, panelGlass, panelBorder);
            Fill(new Rect(rect.x + 12f, rect.y + 14f, 4f, rect.height - 28f), pendingColor);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 16f, rect.width - 56f, 30f), activeProblem.code + " · " + activeProblem.title, titleStyle);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 50f, rect.width - 56f, 24f), "已发现该问题：按 E 检查并就地修复", bodyStyle);
            GUI.Label(new Rect(rect.x + 28f, rect.y + 78f, rect.width - 56f, 22f), "类别：" + activeProblem.category + "    风险：" + LevelText(activeProblem.level) + "    核定经费 ¥" + activeProblem.cost.ToString("N0"), smallStyle);

            Color previous = GUI.color;
            GUI.color = btnBlue;
            GUI.Label(new Rect(rect.x + 28f, rect.y + 108f, 220f, 34f), "按  [E]  检查修复", buttonStyle);
            GUI.color = previous;
            return;
        }

        // 无目标时显示轻提示
        DrawPanel(rect, new Color(0.05f, 0.07f, 0.09f, 0.55f), Color.clear);
        GUI.Label(new Rect(rect.x + 24f, rect.y + 14f, rect.width - 48f, 26f), "前往头顶有红色感叹号的问题点位", centerStyle);
        GUI.Label(new Rect(rect.x + 24f, rect.y + 44f, rect.width - 48f, 22f), "WASD 移动，靠近后按 E 触发修复", centerStyle);
    }

    private void DrawHintBar()
    {
        Rect rect = HintRect;
        Fill(rect, new Color(0.03f, 0.05f, 0.07f, 0.9f));
        GUI.Label(rect, "WASD / 方向键 移动　·　靠近问题点后按 E 检查修复　·　镜头固定俯角自动跟随", centerStyle);
    }

    private void DrawToast()
    {
        if (toastTimer <= 0f)
        {
            return;
        }

        float width = Mathf.Min(Screen.width - 80f, 760f);
        Rect rect = new Rect((Screen.width - width) * 0.5f, Screen.height - 360f, width, 46f);
        DrawPanel(rect, panelFill, panelBorder);
        Fill(new Rect(rect.x + 14f, rect.y + 8f, 4f, rect.height - 16f), toastTimer > 5f ? fixedColor : pendingColor);
        GUI.Label(new Rect(rect.x + 28f, rect.y, rect.width - 44f, rect.height), toastText, toastStyle);
    }

    private static string LevelText(int level)
    {
        if (level >= 3)
        {
            return "严重（安全）";
        }
        return level == 2 ? "中度" : "一般";
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

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 19,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };
        hintStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };
        bodyStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            normal = { textColor = new Color(0.86f, 0.9f, 0.89f) }
        };
        smallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            normal = { textColor = new Color(0.72f, 0.79f, 0.78f) }
        };
        buttonStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white }
        };
        centerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(0.72f, 0.79f, 0.82f) }
        };
        toastStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = true,
            normal = { textColor = new Color(0.88f, 0.92f, 0.91f) }
        };
    }

    // ── 几何生成工具 ──────────────────────────────────────
    private Material MakeMaterial(Color color, float metallic, float smoothness, bool emissive = false)
    {
        Material material = new Material(Shader.Find("Standard"));
        material.color = color;
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Glossiness", smoothness);
        if (emissive)
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color * 0.4f);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }
        return material;
    }

    private Material MakeTransparent(Color color, float alpha, float smoothness)
    {
        Material material = new Material(Shader.Find("Standard"));
        material.SetFloat("_Mode", 3f);
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = 3000;
        material.color = new Color(color.r, color.g, color.b, alpha);
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Glossiness", smoothness);
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
        return CreateCube(objectName, position, scale, MakeMaterial(color, 0.02f, 0.45f));
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

    private GameObject CreateCylinder(string objectName, Vector3 position, float radius, float height, Quaternion rotation, Color color)
    {
        return CreateCylinder(objectName, position, radius, height, rotation, MakeMaterial(color, 0.25f, 0.35f));
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

    // 装饰件：不带碰撞体，避免影响角色移动
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

    private GameObject CreateDecoCylinder(string objectName, Vector3 position, float radius, float height, Quaternion rotation, Color color)
    {
        return CreateDecoCylinder(objectName, position, radius, height, rotation, MakeMaterial(color, 0.2f, 0.4f));
    }

    private GameObject CreateDecoCylinder(string objectName, Vector3 position, float radius, float height, Quaternion rotation, Material material)
    {
        GameObject instance = CreateCylinder(objectName, position, radius, height, rotation, material);
        Collider collider = instance.GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = false;
        }
        return instance;
    }
}
