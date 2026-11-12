using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using HTW.CAVE;
using HTW.CAVE.Kinect;
using JointType = Windows.Kinect.JointType;
using TrackingState = Windows.Kinect.TrackingState;

namespace CaveShow
{
	/// <summary>
	/// Tracking-Demo: teilt den CAVE-Boden in farbige Felder (Standard 4x4) und
	/// zeigt auf der Frontwand an, auf welchem Feld die getrackte Person steht.
	/// Baut alle sichtbaren Objekte zur Laufzeit selbst auf, die Szene braucht nur
	/// das CAVE-Prefab und dieses Skript.
	/// </summary>
	public class FloorFieldDemo : MonoBehaviour
	{
		public enum PositionSource
		{
			/// <summary>Hüfte (SpineBase) auf den Boden projiziert. Am stabilsten.</summary>
			SpineBase,
			/// <summary>Mitte zwischen beiden Knöcheln.</summary>
			FeetCenter
		}

		[Serializable]
		public struct FieldColor
		{
			public string name;
			public Color color;

			public FieldColor(string name, Color color)
			{
				this.name = name;
				this.color = color;
			}
		}

		[Header("CAVE (leer lassen = automatisch suchen)")]
		public VirtualEnvironment environment;
		public KinectTracker tracker;

		[Header("Felder")]
		[Range(1, 6)] public int columns = 4;
		[Range(1, 6)] public int rows = 4;
		[Tooltip("Größe der Spielfläche in Metern (Breite, Tiefe). 0 = ganzer CAVE-Boden.")]
		public Vector2 areaSize = Vector2.zero;
		[Tooltip("Lücke zwischen den Feldern in Metern.")]
		public float gap = 0.04f;
		public FieldColor[] palette =
		{
			new FieldColor("Rot", new Color(1f, 0.15f, 0.12f)),
			new FieldColor("Blau", new Color(0.15f, 0.45f, 1f)),
			new FieldColor("Grün", new Color(0.15f, 0.9f, 0.25f)),
			new FieldColor("Gelb", new Color(1f, 0.85f, 0.1f))
		};
		[Range(0f, 1f)] public float idleBrightness = 0.22f;

		[Header("Tracking")]
		public PositionSource positionSource = PositionSource.SpineBase;
		[Tooltip("So weit (in Metern) muss man über eine Feldgrenze treten, bevor das Feld wechselt. Verhindert Flackern an der Grenze.")]
		public float hysteresis = 0.08f;

		[Header("Anzeige")]
		public bool blackBackground = true;
		[Tooltip("Höhe der Anzeigemitte auf der Frontwand in Metern.")]
		public float frontPanelHeight = 1.25f;
		public float frontPanelScale = 1f;

		[Header("Test ohne Kinect")]
		[Tooltip("Pfeiltasten/WASD bewegen eine Testperson, solange die Kinect niemanden erkennt.")]
		public bool keyboardSimulation = true;
		public float simulationSpeed = 1.2f;

		/// <summary>Feldindex (0 = vorne links, zeilenweise) der ersten Person, -1 = niemand auf einem Feld.</summary>
		public int currentField { get; private set; } = -1;

		/// <summary>Wird ausgelöst, wenn die erste Person das Feld wechselt (neuer Feldindex oder -1).</summary>
		public event Action<int> onFieldChanged;

		private struct Person
		{
			public ulong id;
			public Vector2 position;
			public Vector2 footLeft;
			public Vector2 footRight;
			public float footLeftHeight;
			public float footRightHeight;
			public TrackingState footLeftState;
			public TrackingState footRightState;
			public float createdAt;
			public int field;
		}

		private class PersonMarker
		{
			public GameObject root;
			public Transform body;
			public Transform footLeft;
			public Transform footRight;
			public Material footLeftMaterial;
			public Material footRightMaterial;
			public Transform mapDot;
		}

		private const ulong k_SimulatedId = ulong.MaxValue;

		private static readonly Color s_Tracked = Color.white;
		private static readonly Color s_Inferred = new Color(1f, 1f, 1f, 0.35f);
		private static readonly Color s_LabelActive = new Color(0f, 0f, 0f, 0.6f);
		private static readonly Color s_LabelIdle = new Color(1f, 1f, 1f, 0.45f);

		private readonly List<Person> m_Persons = new List<Person>();
		private readonly List<PersonMarker> m_Markers = new List<PersonMarker>();
		private readonly List<Material> m_Materials = new List<Material>();
		private readonly Dictionary<ulong, int> m_FieldById = new Dictionary<ulong, int>();
		private readonly Dictionary<ulong, int> m_FieldByIdNext = new Dictionary<ulong, int>();
		private readonly StringBuilder m_Text = new StringBuilder(512);

		private KinectManager m_Manager;
		private Transform m_Root;
		private Transform m_FloorRoot;
		private Transform m_MapRoot;
		private Vector2 m_Area;
		private Vector2 m_MapSize;
		private Material[] m_TileMaterials;
		private Material[] m_MapMaterials;
		private TextMesh[] m_TileLabels;
		private bool[] m_Occupied;
		private TextMesh m_Title;
		private TextMesh m_Subtitle;
		private TextMesh m_Info;
		private Mesh m_QuadMesh;
		private Mesh m_DiscMesh;
		private Shader m_Shader;
		private Font m_Font;
		private bool m_SimulationActive;
		private Vector2 m_SimulatedPosition;
		private int m_FieldChanges;
		private float m_NextInfoUpdate;
		private bool m_Dirty = true;

		private int fieldCount => columns * rows;

		private void Start()
		{
			if (environment == null)
				environment = FindObjectOfType<VirtualEnvironment>();

			if (environment == null)
			{
				Debug.LogError("FloorFieldDemo: Kein VirtualEnvironment (CAVE-Prefab) in der Szene gefunden.", this);
				enabled = false;
				return;
			}

			if (tracker == null)
				tracker = environment.GetComponentInChildren<KinectTracker>(true);
			if (tracker == null)
				tracker = FindObjectOfType<KinectTracker>(true);
			if (tracker != null)
				m_Manager = tracker.GetComponent<KinectManager>();

			if (blackBackground)
			{
				foreach (var camera in environment.GetComponentsInChildren<Camera>(true))
				{
					camera.clearFlags = CameraClearFlags.SolidColor;
					camera.backgroundColor = Color.black;
				}
			}

			m_Shader = Shader.Find("Sprites/Default");
			m_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			m_QuadMesh = CreateQuadMesh();
			m_DiscMesh = CreateDiscMesh(40);

			Rebuild();
		}

		private void OnDestroy()
		{
			DestroyVisuals();

			if (m_QuadMesh != null) Destroy(m_QuadMesh);
			if (m_DiscMesh != null) Destroy(m_DiscMesh);
		}

		private void LateUpdate()
		{
			HandleKeys();
			CollectPersons();
			AssignFields();
			UpdateVisuals();
		}

		/// <summary>
		/// Ändert das Raster zur Laufzeit (z. B. 4x4) und baut die Felder neu auf.
		/// </summary>
		public void SetGrid(int newColumns, int newRows)
		{
			columns = Mathf.Clamp(newColumns, 1, 6);
			rows = Mathf.Clamp(newRows, 1, 6);
			Rebuild();
		}

		/// <summary>
		/// Setzt eine Testperson an eine Bodenposition (x = rechts, y = Richtung Frontwand,
		/// in Metern relativ zur CAVE-Mitte). Echte Kinect-Personen haben Vorrang.
		/// </summary>
		public void SetSimulatedPerson(Vector2 floorPosition)
		{
			m_SimulationActive = true;
			m_SimulatedPosition = floorPosition;
		}

		public void ClearSimulatedPerson() => m_SimulationActive = false;

		/// <summary>Farbe eines Feldes laut Palette.</summary>
		public FieldColor GetFieldColor(int field)
		{
			int column = field % columns;
			int row = field / columns;
			// Zeilen versetzen, damit bei z. B. 4 Spalten und 4 Farben keine Streifen entstehen.
			int stride = columns % palette.Length == 0 ? columns + 1 : columns;
			return palette[(column + row * stride) % palette.Length];
		}

		/// <summary>Mittelpunkt eines Feldes auf dem Boden (x = rechts, y = Richtung Frontwand).</summary>
		public Vector2 GetFieldCenter(int field)
		{
			Vector2 cell = new Vector2(m_Area.x / columns, m_Area.y / rows);
			int column = field % columns;
			int row = field / columns;
			return new Vector2(
				-m_Area.x * 0.5f + (column + 0.5f) * cell.x,
				m_Area.y * 0.5f - (row + 0.5f) * cell.y);
		}

		// ------------------------------------------------------------------
		// Eingabe
		// ------------------------------------------------------------------

		private void HandleKeys()
		{
			if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2)) SetGrid(2, 2);
			if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3)) SetGrid(3, 3);
			if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4)) SetGrid(4, 4);

			if (Input.GetKeyDown(KeyCode.P))
			{
				positionSource = positionSource == PositionSource.SpineBase
					? PositionSource.FeetCenter
					: PositionSource.SpineBase;
				m_Dirty = true;
			}

			if (!keyboardSimulation)
				return;

			if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))
				m_SimulationActive = false;

			var move = Vector2.zero;
			if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A)) move.x -= 1f;
			if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D)) move.x += 1f;
			if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W)) move.y += 1f;
			if (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S)) move.y -= 1f;

			if (move != Vector2.zero)
			{
				m_SimulationActive = true;
				m_SimulatedPosition += move.normalized * (simulationSpeed * Time.deltaTime);
				m_SimulatedPosition.x = Mathf.Clamp(m_SimulatedPosition.x, -m_Area.x * 0.5f - 0.3f, m_Area.x * 0.5f + 0.3f);
				m_SimulatedPosition.y = Mathf.Clamp(m_SimulatedPosition.y, -m_Area.y * 0.5f - 0.3f, m_Area.y * 0.5f + 0.3f);
			}
		}

		// ------------------------------------------------------------------
		// Tracking
		// ------------------------------------------------------------------

		private void CollectPersons()
		{
			m_Persons.Clear();

			if (tracker != null && tracker.isActiveAndEnabled)
			{
				var actors = tracker.actors;

				for (int i = 0; i < actors.Count; ++i)
				{
					var actor = actors[i];

					if (actor == null)
						continue;

					var spine = actor.GetJoint(JointType.SpineBase);

					if (spine.trackingState == TrackingState.NotTracked)
						continue;

					var left = actor.GetJoint(JointType.AnkleLeft);
					var right = actor.GetJoint(JointType.AnkleRight);
					Vector3 spineLocal = ToLocal(spine.position);
					Vector3 leftLocal = ToLocal(left.position);
					Vector3 rightLocal = ToLocal(right.position);

					var person = new Person
					{
						id = actor.trackingId,
						createdAt = actor.createdAt,
						position = new Vector2(spineLocal.x, spineLocal.z),
						footLeft = new Vector2(leftLocal.x, leftLocal.z),
						footRight = new Vector2(rightLocal.x, rightLocal.z),
						footLeftHeight = leftLocal.y,
						footRightHeight = rightLocal.y,
						footLeftState = left.trackingState,
						footRightState = right.trackingState,
						field = -1
					};

					if (positionSource == PositionSource.FeetCenter)
					{
						bool hasLeft = left.trackingState != TrackingState.NotTracked;
						bool hasRight = right.trackingState != TrackingState.NotTracked;

						if (hasLeft && hasRight) person.position = (person.footLeft + person.footRight) * 0.5f;
						else if (hasLeft) person.position = person.footLeft;
						else if (hasRight) person.position = person.footRight;
					}

					m_Persons.Add(person);
				}

				// Älteste Person zuerst: sie ist die "Hauptperson" für die Anzeige.
				m_Persons.Sort((a, b) => a.createdAt.CompareTo(b.createdAt));
			}

			if (m_Persons.Count == 0 && m_SimulationActive)
			{
				m_Persons.Add(new Person
				{
					id = k_SimulatedId,
					position = m_SimulatedPosition,
					footLeft = m_SimulatedPosition + new Vector2(-0.1f, 0f),
					footRight = m_SimulatedPosition + new Vector2(0.1f, 0f),
					footLeftState = TrackingState.Tracked,
					footRightState = TrackingState.Tracked,
					field = -1
				});
			}
		}

		private Vector3 ToLocal(Vector3 worldPosition) =>
			environment.transform.InverseTransformPoint(worldPosition);

		private void AssignFields()
		{
			m_FieldByIdNext.Clear();

			for (int i = 0; i < m_Persons.Count; ++i)
			{
				var person = m_Persons[i];
				bool known = m_FieldById.TryGetValue(person.id, out int previous);

				person.field = known && previous >= 0 && previous < fieldCount && IsInsideField(person.position, previous, hysteresis)
					? previous
					: FieldAt(person.position);

				m_Persons[i] = person;
				m_FieldByIdNext[person.id] = person.field;
			}

			m_FieldById.Clear();
			foreach (var pair in m_FieldByIdNext)
				m_FieldById[pair.Key] = pair.Value;

			int field = m_Persons.Count > 0 ? m_Persons[0].field : -1;

			if (field != currentField)
			{
				currentField = field;
				if (field >= 0) ++m_FieldChanges;
				m_Dirty = true;
				onFieldChanged?.Invoke(field);
			}
		}

		private int FieldAt(Vector2 position)
		{
			float u = (position.x + m_Area.x * 0.5f) / m_Area.x;
			float v = (m_Area.y * 0.5f - position.y) / m_Area.y;

			if (u < 0f || u >= 1f || v < 0f || v >= 1f)
				return -1;

			return Mathf.Min((int)(v * rows), rows - 1) * columns + Mathf.Min((int)(u * columns), columns - 1);
		}

		private bool IsInsideField(Vector2 position, int field, float margin)
		{
			Vector2 center = GetFieldCenter(field);
			Vector2 half = new Vector2(m_Area.x / columns, m_Area.y / rows) * 0.5f + new Vector2(margin, margin);

			return Mathf.Abs(position.x - center.x) <= half.x && Mathf.Abs(position.y - center.y) <= half.y;
		}

		// ------------------------------------------------------------------
		// Anzeige aktualisieren
		// ------------------------------------------------------------------

		private void UpdateVisuals()
		{
			for (int i = 0; i < m_Occupied.Length; ++i)
				m_Occupied[i] = false;

			for (int i = 0; i < m_Persons.Count; ++i)
				if (m_Persons[i].field >= 0)
					m_Occupied[m_Persons[i].field] = true;

			for (int i = 0; i < m_Occupied.Length; ++i)
			{
				Color color = GetFieldColor(i).color;
				Color shown = m_Occupied[i] ? color : Dim(color, idleBrightness);
				m_TileMaterials[i].color = shown;
				m_MapMaterials[i].color = shown;
				m_TileLabels[i].color = TextColor(m_Occupied[i] ? s_LabelActive : s_LabelIdle);
			}

			while (m_Markers.Count < m_Persons.Count)
				m_Markers.Add(CreateMarker(m_Markers.Count));

			for (int i = 0; i < m_Markers.Count; ++i)
			{
				var marker = m_Markers[i];
				bool active = i < m_Persons.Count;

				marker.root.SetActive(active);
				marker.mapDot.gameObject.SetActive(active);

				if (!active)
					continue;

				var person = m_Persons[i];
				marker.body.localPosition = new Vector3(person.position.x, 0f, person.position.y);
				PlaceFoot(marker.footLeft, marker.footLeftMaterial, person.footLeft, person.footLeftState);
				PlaceFoot(marker.footRight, marker.footRightMaterial, person.footRight, person.footRightState);
				marker.mapDot.localPosition = new Vector3(
					Mathf.Clamp(person.position.x / m_Area.x, -0.6f, 0.6f) * m_MapSize.x,
					Mathf.Clamp(person.position.y / m_Area.y, -0.6f, 0.6f) * m_MapSize.y,
					0f);
			}

			if (m_Dirty)
			{
				m_Dirty = false;
				UpdateTitle();
				m_NextInfoUpdate = 0f;
			}

			if (Time.unscaledTime >= m_NextInfoUpdate)
			{
				m_NextInfoUpdate = Time.unscaledTime + 0.15f;
				UpdateInfo();
			}
		}

		private static void PlaceFoot(Transform foot, Material material, Vector2 position, TrackingState state)
		{
			foot.gameObject.SetActive(state != TrackingState.NotTracked);
			foot.localPosition = new Vector3(position.x, 0f, position.y);
			material.color = state == TrackingState.Tracked ? s_Tracked : s_Inferred;
		}

		private void UpdateTitle()
		{
			if (currentField >= 0)
			{
				var color = GetFieldColor(currentField);
				m_Title.text = "FELD " + (currentField + 1);
				m_Title.color = TextColor(color.color);
				m_Subtitle.text = color.name + "  ·  " + DescribeField(currentField);
				m_Subtitle.color = Color.white;
			}
			else if (m_Persons.Count > 0)
			{
				m_Title.text = "–";
				m_Title.color = Color.gray;
				m_Subtitle.text = "Person außerhalb der Felder";
				m_Subtitle.color = Color.gray;
			}
			else
			{
				m_Title.text = "–";
				m_Title.color = Color.gray;
				m_Subtitle.text = "Keine Person erkannt";
				m_Subtitle.color = Color.gray;
			}
		}

		private string DescribeField(int field)
		{
			int column = field % columns;
			int row = field / columns;

			if (columns == 2 && rows == 2)
				return (row == 0 ? "vorne " : "hinten ") + (column == 0 ? "links" : "rechts");

			return "Reihe " + (row + 1) + ", Spalte " + (column + 1);
		}

		private void UpdateInfo()
		{
			var culture = CultureInfo.InvariantCulture;
			m_Text.Length = 0;

			m_Text.Append("Kinect: ").Append(DescribeSensor());
			m_Text.Append("   ·   Personen: ").Append(m_Persons.Count);
			m_Text.Append("   ·   Raster: ").Append(columns).Append('x').Append(rows);
			m_Text.Append("   ·   Feldwechsel: ").Append(m_FieldChanges).Append('\n');

			m_Text.Append("Boden: ").Append(DescribeFloor(culture)).Append('\n');

			if (m_Persons.Count > 0)
			{
				var person = m_Persons[0];
				bool simulated = person.id == k_SimulatedId;

				m_Text.Append(simulated ? "Testperson (Tastatur)" : "Position");
				m_Text.Append(positionSource == PositionSource.SpineBase ? " [Hüfte]" : " [Fußmitte]");
				m_Text.Append(":  x = ").Append(person.position.x.ToString("+0.00;-0.00", culture));
				m_Text.Append(" m   z = ").Append(person.position.y.ToString("+0.00;-0.00", culture)).Append(" m\n");

				if (!simulated)
				{
					m_Text.Append("Knöchel L: ").Append(DescribeFoot(person.footLeftState, person.footLeftHeight, culture));
					m_Text.Append("   ·   Knöchel R: ").Append(DescribeFoot(person.footRightState, person.footRightHeight, culture));
				}

				if (m_Persons.Count > 1)
				{
					m_Text.Append(simulated ? "" : "   ·   ").Append("Felder aller Personen:");
					for (int i = 0; i < m_Persons.Count; ++i)
						m_Text.Append(' ').Append(m_Persons[i].field >= 0 ? (m_Persons[i].field + 1).ToString() : "–");
				}
			}
			else if (keyboardSimulation)
			{
				m_Text.Append("Test ohne Kinect: Pfeiltasten oder WASD bewegen eine Testperson.");
			}

			m_Info.text = m_Text.ToString();
		}

		private string DescribeSensor()
		{
			if (tracker == null || m_Manager == null)
				return "kein Kinect Tracker in der Szene";

			try
			{
				var sensor = m_Manager.sensor;

				if (sensor == null)
					return "nicht verfügbar (SDK/Sensor fehlt)";

				return sensor.IsAvailable ? "verbunden" : "SDK ok, Sensor nicht gefunden";
			}
			catch (Exception)
			{
				return "nicht verfügbar";
			}
		}

		private string DescribeFloor(CultureInfo culture)
		{
			if (m_Manager == null || m_Manager.sensor == null)
				return "–";

			UnityEngine.Vector4 plane = m_Manager.floorClipPlane;

			if (Mathf.Approximately(plane.sqrMagnitude, 0f))
				return "NICHT erkannt (Kinect sieht den Boden nicht)";

			return "erkannt   ·   Sensorhöhe " + plane.w.ToString("0.00", culture)
				+ " m   ·   Neigung " + m_Manager.tilt.ToString("0.0", culture) + "°";
		}

		private static string DescribeFoot(TrackingState state, float height, CultureInfo culture)
		{
			switch (state)
			{
				case TrackingState.Tracked:
					return "getrackt, " + height.ToString("0.00", culture) + " m über Boden";
				case TrackingState.Inferred:
					return "geschätzt, " + height.ToString("0.00", culture) + " m über Boden";
				default:
					return "nicht erkannt";
			}
		}

		// ------------------------------------------------------------------
		// Aufbau der sichtbaren Objekte
		// ------------------------------------------------------------------

		private void Rebuild()
		{
			DestroyVisuals();

			Vector3 dimensions = environment.dimensions;
			m_Area = new Vector2(
				areaSize.x > 0f ? areaSize.x : dimensions.x,
				areaSize.y > 0f ? areaSize.y : dimensions.z);

			m_Root = new GameObject("Floor Field Demo Visuals").transform;
			m_Root.SetParent(environment.transform, false);

			m_Occupied = new bool[fieldCount];
			m_TileMaterials = new Material[fieldCount];
			m_MapMaterials = new Material[fieldCount];
			m_TileLabels = new TextMesh[fieldCount];
			m_FieldById.Clear();
			currentField = -1;

			BuildFloor();
			BuildFrontPanel(dimensions);

			m_Dirty = true;
		}

		private void DestroyVisuals()
		{
			if (m_Root != null)
				Destroy(m_Root.gameObject);

			foreach (var material in m_Materials)
				Destroy(material);

			m_Materials.Clear();
			m_Markers.Clear();
			m_Root = null;
		}

		private void BuildFloor()
		{
			m_FloorRoot = new GameObject("Floor").transform;
			m_FloorRoot.SetParent(m_Root, false);

			Vector2 cell = new Vector2(m_Area.x / columns, m_Area.y / rows);
			float labelHeight = Mathf.Min(cell.x, cell.y) * 0.35f;

			for (int i = 0; i < fieldCount; ++i)
			{
				Vector2 center = GetFieldCenter(i);
				var position = new Vector3(center.x, 0f, center.y);

				m_TileMaterials[i] = CreateQuad("Feld " + (i + 1), m_FloorRoot, position,
					Quaternion.Euler(90f, 0f, 0f), new Vector2(cell.x - gap, cell.y - gap), Color.black, 0);

				m_TileLabels[i] = CreateText("Nummer", m_FloorRoot, position, Quaternion.Euler(90f, 0f, 0f),
					labelHeight, TextAnchor.MiddleCenter, s_LabelIdle, 1);
				m_TileLabels[i].text = (i + 1).ToString();
			}
		}

		private void BuildFrontPanel(Vector3 dimensions)
		{
			var panel = new GameObject("Front Panel").transform;
			panel.SetParent(m_Root, false);
			// Knapp hinter der Frontwand, damit die Anzeige ohne Parallaxe auf der Leinwand liegt.
			panel.localPosition = new Vector3(0f, frontPanelHeight, dimensions.z * 0.5f + 0.01f);
			panel.localScale = Vector3.one * frontPanelScale;

			m_Title = CreateText("Titel", panel, new Vector3(0f, 0.52f, 0f), Quaternion.identity,
				0.4f, TextAnchor.MiddleCenter, Color.gray, 1);
			m_Title.fontStyle = FontStyle.Bold;

			m_Subtitle = CreateText("Untertitel", panel, new Vector3(0f, 0.2f, 0f), Quaternion.identity,
				0.12f, TextAnchor.MiddleCenter, Color.gray, 1);

			// Mini-Karte: Draufsicht auf den Boden, Frontwand ist oben.
			m_MapRoot = new GameObject("Karte").transform;
			m_MapRoot.SetParent(panel, false);
			m_MapRoot.localPosition = new Vector3(0f, -0.26f, 0f);

			float mapScale = 0.56f / Mathf.Max(m_Area.x, m_Area.y);
			m_MapSize = m_Area * mapScale;
			Vector2 cell = new Vector2(m_MapSize.x / columns, m_MapSize.y / rows);

			for (int i = 0; i < fieldCount; ++i)
			{
				Vector2 center = GetFieldCenter(i) * mapScale;
				m_MapMaterials[i] = CreateQuad("Feld " + (i + 1), m_MapRoot, center,
					Quaternion.identity, cell - Vector2.one * 0.012f, Color.black, 0);
			}

			m_Info = CreateText("Info", panel, new Vector3(0f, -0.64f, 0f), Quaternion.identity,
				0.05f, TextAnchor.UpperCenter, new Color(0.85f, 0.85f, 0.85f), 1);
			m_Info.alignment = TextAlignment.Center;
			m_Info.lineSpacing = 1.15f;

			CreateText("Hilfe", panel, new Vector3(0f, -0.98f, 0f), Quaternion.identity,
				0.036f, TextAnchor.UpperCenter, new Color(0.5f, 0.5f, 0.5f), 1).text =
				"Tasten:  2 / 3 / 4 = Raster 2x2 / 3x3 / 4x4   ·   P = Hüfte / Fußmitte   ·   Pfeiltasten = Testperson   ·   Entf = Testperson aus";
		}

		private PersonMarker CreateMarker(int index)
		{
			var marker = new PersonMarker();

			marker.root = new GameObject("Person " + (index + 1));
			marker.root.transform.SetParent(m_FloorRoot, false);

			CreateDisc("Position", marker.root.transform, 0.13f, new Color(1f, 1f, 1f, 0.9f), 2, out marker.body, true);
			marker.footLeftMaterial = CreateDisc("Knöchel L", marker.root.transform, 0.045f, s_Tracked, 4, out marker.footLeft, true);
			marker.footRightMaterial = CreateDisc("Knöchel R", marker.root.transform, 0.045f, s_Tracked, 4, out marker.footRight, true);

			// Schwarzer Kern, damit der Positionsring auch auf hellen Feldern erkennbar bleibt.
			CreateDisc("Kern", marker.body, 0.7f, Color.black, 3, out _, false);

			CreateDisc("Person " + (index + 1), m_MapRoot, 0.022f, Color.white, 2, out marker.mapDot, false);

			return marker;
		}

		private Material CreateDisc(string name, Transform parent, float radius, Color color, int order,
			out Transform transform, bool onFloor)
		{
			var material = CreateMeshObject(name, parent, m_DiscMesh, color, order, out transform);
			transform.localRotation = onFloor ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
			transform.localScale = new Vector3(radius, radius, 1f);
			return material;
		}

		private Material CreateQuad(string name, Transform parent, Vector3 localPosition, Quaternion localRotation,
			Vector2 size, Color color, int order)
		{
			var material = CreateMeshObject(name, parent, m_QuadMesh, color, order, out Transform transform);
			transform.localPosition = localPosition;
			transform.localRotation = localRotation;
			transform.localScale = new Vector3(size.x, size.y, 1f);
			return material;
		}

		private Material CreateMeshObject(string name, Transform parent, Mesh mesh, Color color, int order,
			out Transform transform)
		{
			var gameObject = new GameObject(name);
			transform = gameObject.transform;
			transform.SetParent(parent, false);

			var material = new Material(m_Shader) { color = color };
			m_Materials.Add(material);

			gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
			var renderer = gameObject.AddComponent<MeshRenderer>();
			renderer.sharedMaterial = material;
			renderer.sortingOrder = order;
			renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			renderer.receiveShadows = false;

			return material;
		}

		private TextMesh CreateText(string name, Transform parent, Vector3 localPosition, Quaternion localRotation,
			float height, TextAnchor anchor, Color color, int order)
		{
			const int fontSize = 96;

			var gameObject = new GameObject(name);
			gameObject.transform.SetParent(parent, false);
			gameObject.transform.localPosition = localPosition;
			gameObject.transform.localRotation = localRotation;

			var text = gameObject.AddComponent<TextMesh>();
			text.font = m_Font;
			text.fontSize = fontSize;
			text.characterSize = height * 10f / fontSize;
			text.anchor = anchor;
			text.color = TextColor(color);

			var renderer = gameObject.GetComponent<MeshRenderer>();
			renderer.sharedMaterial = m_Font.material;
			renderer.sortingOrder = order;

			return text;
		}

		/// <summary>
		/// TextMesh rechnet seine Farbe im linearen Farbraum nicht um, dadurch wirkt Text
		/// heller als gleichfarbige Flächen. Hier wird das ausgeglichen.
		/// </summary>
		private static Color TextColor(Color color) =>
			QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color;

		private static Color Dim(Color color, float brightness) =>
			new Color(color.r * brightness, color.g * brightness, color.b * brightness, 1f);

		private static Mesh CreateQuadMesh()
		{
			var mesh = new Mesh { name = "FloorDemo Quad" };
			mesh.vertices = new[]
			{
				new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
				new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)
			};
			mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
			mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
			mesh.RecalculateBounds();
			return mesh;
		}

		/// <summary>Kreisscheibe mit Radius 1 in der XY-Ebene.</summary>
		private static Mesh CreateDiscMesh(int segments)
		{
			var vertices = new Vector3[segments + 1];
			var uv = new Vector2[segments + 1];
			var triangles = new int[segments * 3];

			vertices[0] = Vector3.zero;
			uv[0] = new Vector2(0.5f, 0.5f);

			for (int i = 0; i < segments; ++i)
			{
				float angle = i * Mathf.PI * 2f / segments;
				var point = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
				vertices[i + 1] = point;
				uv[i + 1] = point * 0.5f + new Vector2(0.5f, 0.5f);
				triangles[i * 3] = 0;
				triangles[i * 3 + 1] = 1 + (i + 1) % segments;
				triangles[i * 3 + 2] = 1 + i;
			}

			var mesh = new Mesh { name = "FloorDemo Disc" };
			mesh.vertices = vertices;
			mesh.uv = uv;
			mesh.triangles = triangles;
			mesh.RecalculateBounds();
			return mesh;
		}
	}
}
