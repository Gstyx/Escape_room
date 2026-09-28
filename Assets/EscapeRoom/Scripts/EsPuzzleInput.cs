using UnityEngine;

namespace EscapeRoom
{
    /// <summary>Marks a branch of UI as puzzle input, which is the player typing an answer.
    ///
    /// Added to the keypad root. It exists to separate two things that look identical from the
    /// outside and must behave differently:
    ///
    ///   "I want clicking a wall panel from across the room to be forgiving" - a real complaint, and
    ///   true. A world-space canvas button shrinks with distance and no setting changes that.
    ///
    ///   "I want pressing one key to type the code for me" - not a difficulty setting, it is the
    ///   puzzle answered. The keypad IS a canvas, so any aiming aid that reaches it reaches the
    ///   answer.
    ///
    /// So forgiveness is applied per candidate rather than globally: non-puzzle targets get the wide
    /// radius, puzzle input gets only the tight one. That is the whole argument in one marker, and
    /// it is why the interact key could be removed without giving up the reachable terminal.
    ///
    /// A tag would also work, but tags are a project-wide string namespace with no compile-time
    /// check, and a typo would silently mark nothing - which here would silently delete the
    /// player's puzzle.
    /// </summary>
    public class EsPuzzleInput : MonoBehaviour
    {
        public string what = "answer entry";
    }
}
