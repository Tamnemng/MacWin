using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace MacWin.UI
{
    /// <summary>
    /// GPU pixel shader that turns a (pre-blurred) background into a slab of curved glass. Ported to HLSL from the
    /// WebGL shader of "liquid-glass" by Archis Vaze (https://github.com/archisvaze/liquid-glass, MIT licence):
    /// a squircle-like surface height profile across the bezel, Snell refraction with an index of refraction,
    /// a 16-tap Poisson blur, a specular rim facing the light, an inner shadow and a thin inner rim.
    /// Compiled at runtime with the d3dcompiler that ships with Windows, so no build tools are needed.
    ///
    /// Portions Copyright (c) 2026 Archis Vaze — MIT License:
    /// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
    /// documentation files (the "Software"), to deal in the Software without restriction, including without limitation
    /// the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
    /// to permit persons to whom the Software is furnished to do so, subject to the following conditions:
    /// The above copyright notice and this permission notice shall be included in all copies or substantial portions
    /// of the Software.
    /// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
    /// THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    /// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
    /// CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
    /// DEALINGS IN THE SOFTWARE.
    /// </summary>
    public class LiquidGlassEffect : ShaderEffect
    {
        const string Hlsl = @"
sampler2D input : register(s0);
float4 SizeRadius : register(c0);   // x,y size px · z corner radius px · w bezel width px
float4 Params     : register(c1);   // x glass thickness px · y index of refraction · z specular · w tint towards white
float4 LightDir   : register(c2);   // xy light direction (y down) · z extra blur px · w saturation boost

static const float2 Poisson[16] = {
    float2(-0.94201, -0.39906), float2( 0.94558, -0.76890), float2(-0.09418, -0.92938), float2( 0.34495,  0.29387),
    float2(-0.91588, -0.45771), float2(-0.81544,  0.48568), float2(-0.38277, -0.56071), float2(-0.12675,  0.84686),
    float2( 0.89642,  0.41254), float2( 0.18150, -0.30020), float2(-0.01445, -0.16001), float2( 0.59614,  0.71118),
    float2( 0.49742, -0.47280), float2( 0.80685,  0.04588), float2(-0.32490, -0.03965), float2(-0.60975,  0.06566) };

float sdRoundedRect(float2 p, float2 halfSize, float r)
{
    float2 q = abs(p) - halfSize + float2(r, r);
    return min(max(q.x, q.y), 0.0) + length(max(q, float2(0.0, 0.0))) - r;
}

float surfaceHeight(float t)
{
    float s = 1.0 - t;
    return pow(max(1.0 - s * s * s * s, 0.0), 0.25);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 size = max(SizeRadius.xy, float2(2.0, 2.0));
    float2 halfSize = size * 0.5;
    float radius = min(SizeRadius.z, min(halfSize.x, halfSize.y));
    float2 p = (uv - 0.5) * size;
    float sd = sdRoundedRect(p, halfSize, radius);
    float distFromEdge = max(-sd, 0.0);

    float bezel = max(min(SizeRadius.w, min(radius, min(halfSize.x, halfSize.y)) - 1.0), 1.0);
    float t = saturate(distFromEdge / bezel);

    // refraction through the curved bezel (Snell's law with the glass' index of refraction)
    float h = surfaceHeight(t);
    float dt = 0.001;
    float dh = (surfaceHeight(min(t + dt, 1.0)) - h) / dt;
    float thickness = Params.x;
    float slopeAngle = atan(dh * (thickness / bezel));
    float sinR = clamp(sin(slopeAngle) / max(Params.y, 1.0), -1.0, 1.0);
    float thetaR = asin(sinR);
    float displacement = h * thickness * (tan(slopeAngle) - tan(thetaR));

    float eps = 0.5;
    float2 grad = float2(sdRoundedRect(p + float2(eps, 0.0), halfSize, radius) - sd,
                         sdRoundedRect(p + float2(0.0, eps), halfSize, radius) - sd);
    grad = grad / max(length(grad), 0.0001);

    float2 refracted = uv - grad * displacement / size;

    // light extra blur (the backdrop is already frosted)
    float3 color = float3(0.0, 0.0, 0.0);
    float alphaIn = 0.0;
    float2 px = LightDir.z / size;
    [unroll] for (int i = 0; i < 16; i++)
    {
        float4 c = tex2D(input, refracted + Poisson[i] * px);
        color += c.rgb;
        alphaIn += c.a;
    }
    color /= 16.0;
    alphaIn /= 16.0;
    color = color / max(alphaIn, 0.001);   // un-premultiply

    // vibrancy: what is seen through the glass is slightly more saturated
    float luma = dot(color, float3(0.299, 0.587, 0.114));
    color = lerp(float3(luma, luma, luma), color, 1.0 + LightDir.w);

    // specular rim facing the light
    float2 lightDir = normalize(LightDir.xy);
    float rimDot = abs(dot(grad, lightDir));
    float rimFalloff = 1.0 - smoothstep(0.0, bezel * 0.4, distFromEdge);
    float specHighlight = pow(max(rimDot * rimFalloff, 0.0), 1.5);
    color += specHighlight * Params.z;

    // inner shadow along the bezel
    float innerShadow = 1.0 - smoothstep(0.0, bezel * 0.6, distFromEdge);
    color *= lerp(1.0, 0.7, innerShadow * 0.3);

    // thin bright line just inside the edge
    float innerRim = smoothstep(0.0, 2.0, distFromEdge) * (1.0 - smoothstep(2.0, 5.0, distFromEdge));
    color += innerRim * 0.15 * Params.z;

    color = lerp(color, float3(1.0, 1.0, 1.0), Params.w);

    float a = smoothstep(0.0, 1.5, distFromEdge) * saturate(alphaIn);
    return float4(saturate(color) * a, a);
}";

        static PixelShader _shader;
        static bool _tried;

        public static bool Available
        {
            get { Ensure(); return _shader != null; }
        }

        static void Ensure()
        {
            if (_tried) return;
            _tried = true;
            try
            {
                if (!RenderCapability.IsPixelShaderVersionSupported(3, 0)) return;
                var bytes = Compile(Hlsl, "ps_3_0");
                if (bytes == null) return;
                var ps = new PixelShader();
                ps.SetStreamSource(new MemoryStream(bytes));
                ps.Freeze();
                _shader = ps;
            }
            catch (Exception e) { App.Log(e); }
        }

        // ---- runtime HLSL compilation through d3dcompiler_47.dll (part of Windows 10/11)
        [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID3DBlob
        {
            [PreserveSig] IntPtr GetBufferPointer();
            [PreserveSig] IntPtr GetBufferSize();
        }

        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
        static extern int D3DCompile(byte[] src, IntPtr srcSize, string sourceName, IntPtr defines, IntPtr include,
            string entryPoint, string target, uint flags1, uint flags2, out ID3DBlob code, out ID3DBlob errors);

        static byte[] Compile(string source, string target)
        {
            var src = Encoding.ASCII.GetBytes(source);
            int hr = D3DCompile(src, (IntPtr)src.Length, "LiquidGlass", IntPtr.Zero, IntPtr.Zero, "main", target, 1u << 15 /*OPTIMIZATION_LEVEL3*/, 0, out var code, out var errors);
            try
            {
                if (hr < 0 || code == null)
                {
                    if (errors != null) App.Log(new Exception("Shader: " + Marshal.PtrToStringAnsi(errors.GetBufferPointer())));
                    return null;
                }
                var bytes = new byte[(int)code.GetBufferSize()];
                Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                if (code != null) Marshal.ReleaseComObject(code);
                if (errors != null) Marshal.ReleaseComObject(errors);
            }
        }

        // ---- shader inputs
        public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(LiquidGlassEffect), 0, SamplingMode.Bilinear);
        public static readonly DependencyProperty SizeRadiusProperty = DependencyProperty.Register("SizeRadius", typeof(Point4D), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(new Point4D(300, 200, 60, 60), PixelShaderConstantCallback(0)));
        public static readonly DependencyProperty ParamsProperty = DependencyProperty.Register("Params", typeof(Point4D), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(new Point4D(50, 3.0, 0.55, 0.08), PixelShaderConstantCallback(1)));
        public static readonly DependencyProperty LightDirProperty = DependencyProperty.Register("LightDir", typeof(Point4D), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(new Point4D(0.5, -0.7, 1.5, 0.15), PixelShaderConstantCallback(2)));

        public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
        public Point4D SizeRadius { get => (Point4D)GetValue(SizeRadiusProperty); set => SetValue(SizeRadiusProperty, value); }
        public Point4D Params { get => (Point4D)GetValue(ParamsProperty); set => SetValue(ParamsProperty, value); }
        public Point4D LightDir { get => (Point4D)GetValue(LightDirProperty); set => SetValue(LightDirProperty, value); }

        public LiquidGlassEffect()
        {
            Ensure();
            PixelShader = _shader;
            Input = ImplicitInput; // the element's own rendering (the wallpaper slice)
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(SizeRadiusProperty);
            UpdateShaderValue(ParamsProperty);
            UpdateShaderValue(LightDirProperty);
        }
    }
}
