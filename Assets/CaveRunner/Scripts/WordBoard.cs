using UnityEngine;

namespace CaveRunner
{
    /// <summary>
    /// Eine Wort-Tafel, die über der Strecke auf die Person zukommt
    /// (langsamer als die Hindernisse, siehe Handprototyp S. 3).
    /// </summary>
    public class WordBoard : MonoBehaviour
    {
        public enum BoardState { Waiting, Correct, Missed }

        public string Word { get; private set; }
        public BoardState State { get; private set; } = BoardState.Waiting;
        public float speed;

        Renderer frame;
        TextMesh text;

        public static WordBoard Create(Transform parent, string word, Vector3 localPos, float speed)
        {
            var root = new GameObject("WordBoard " + word).transform;
            root.SetParent(parent, false);
            root.localPosition = localPos;

            var board = root.gameObject.AddComponent<WordBoard>();
            board.Word = word;
            board.speed = speed;

            board.frame = RunnerUtil.Box("Rahmen", root, Vector3.zero, new Vector3(3.2f, 1.1f, 0.1f),
                RunnerUtil.Mat(new Color(1f, 0.6f, 0.15f), 1.2f)).GetComponent<Renderer>();
            RunnerUtil.Box("Tafel", root, new Vector3(0, 0, -0.06f), new Vector3(3f, 0.9f, 0.05f),
                RunnerUtil.Mat(new Color(0.98f, 0.95f, 0.88f), 0.3f));
            board.text = RunnerUtil.Text("Wort", root, new Vector3(0, 0, -0.1f), 0.11f, new Color(0.1f, 0.1f, 0.1f));
            board.text.text = word;
            board.text.fontStyle = FontStyle.Bold;
            return board;
        }

        public void SetState(BoardState state)
        {
            State = state;
            Color c = state == BoardState.Correct ? new Color(0.2f, 0.85f, 0.3f)
                    : state == BoardState.Missed ? new Color(0.9f, 0.15f, 0.15f)
                    : new Color(1f, 0.6f, 0.15f);
            frame.sharedMaterial = RunnerUtil.Mat(c, 1.2f);
        }

        void Update()
        {
            transform.localPosition += Vector3.back * speed * Time.deltaTime;
        }
    }
}
