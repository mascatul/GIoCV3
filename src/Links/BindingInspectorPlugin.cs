#if TOOLS
using Godot;

namespace GIoC.Links;

[Tool]
public partial class BindingInspectorPlugin : EditorPlugin
{
    private LinkConfigInspectorPlugin _inspectorPlugin;

    public override void _EnterTree()
    {
        _inspectorPlugin = new LinkConfigInspectorPlugin();
        AddInspectorPlugin(_inspectorPlugin);
    }

    public override void _ExitTree()
    {
        if (_inspectorPlugin != null)
        {
            RemoveInspectorPlugin(_inspectorPlugin);
            _inspectorPlugin = null;
        }
    }
}
#endif
