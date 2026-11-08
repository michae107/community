// RimWorld voice commands. rimworld.py loads this file into the game once, with
// `rimworld-exec --load`, then each command calls one of these methods with `--call`, so
// nothing is compiled per command. Saving changes here takes effect on the next command.
//
// Returning a string shows it as a Talon notification; returning null shows nothing.

public static class Voice
{
    // Pans the camera by (dx, dz) thirds of the screen, smoothly. If a pan is still moving, the
    // new one starts from where that one is headed, so quick repeats add up.
    public static string Look(int dx, int dz)
    {
        Map map = Find.CurrentMap;
        if (map == null || WorldRendererUtility.WorldSelected)
        {
            return "No map is showing.";
        }
        CameraDriver camera = Find.CameraDriver;
        Vector3 from = camera.panner.Moving ? camera.panner.Destination.Position : camera.rootPos;
        // RootSize is half the screen's height in cells.
        float third = camera.RootSize * 2f / 3f;
        float aspect = (float)UI.screenWidth / UI.screenHeight;
        Vector3 to = from + new Vector3(dx * third * aspect, 0f, dz * third);
        to.x = Mathf.Clamp(to.x, 2f, map.Size.x - 2f);
        to.z = Mathf.Clamp(to.z, 2f, map.Size.z - 2f);
        camera.PanToMapLocAndSize(to, camera.RootSize);
        return null;
    }

    // Selects the nth colonist in colonist bar order and moves the camera to them.
    public static string Colonist(int n)
    {
        List<Pawn> colonists = Find.ColonistBar.GetColonistsInOrder();
        if (n < 1 || n > colonists.Count)
        {
            return $"There are only {colonists.Count} colonists.";
        }
        CameraJumper.TryJumpAndSelect(colonists[n - 1]);
        return null;
    }
}
