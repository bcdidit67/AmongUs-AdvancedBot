using BepInEx;
using BepInEx.Unity.IL2CPP;

[BepInPlugin("com.envcheck.smoke", "EnvCheck", "1.0.0")]
public class Plugin : BasePlugin
{
    public override void Load()
    {
        Log.LogInfo("[EnvCheck] BepInEx plugin loaded OK");
        // 触碰一个游戏 interop 类型，验证「字典」真的可用
        Log.LogInfo($"[EnvCheck] game type resolved: {typeof(PlayerControl).FullName}");
    }
}
