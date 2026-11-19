using Godot;

namespace Sanguo.Client;

/// <summary>保持铜镜纹样的圆形比例，压低底图反差以衬出角色与卡面。</summary>
public partial class BattleTableBackdrop : TextureRect
{
    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ExpandMode = ExpandModeEnum.IgnoreSize;
        StretchMode = StretchModeEnum.KeepAspectCovered;
        Material = new ShaderMaterial { Shader = new Shader { Code = """
            shader_type canvas_item;
            void fragment() {
                vec3 metal = texture(TEXTURE, UV).rgb;
                float light = dot(metal, vec3(0.299, 0.587, 0.114));
                float shade = smoothstep(0.0, 0.65, light);
                COLOR = vec4(mix(vec3(0.21, 0.17, 0.13), vec3(0.61, 0.51, 0.39), shade), 1.0);
            }
            """ } };
    }
}
