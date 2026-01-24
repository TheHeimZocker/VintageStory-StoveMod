using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace StoveMod.API
{
    /// <summary>
    /// Adapter that wraps an IInFirepitRenderer to work as an IStoveTopRenderer.
    /// Handles rendering of pot, lid, and content meshes on the stove top.
    /// 
    /// If the wrapped renderer implements IProvidesStoveMeshes, those properties are used.
    /// Otherwise, attempts reflection to find common field names (potMeshRef, contentMeshRef, lidMeshRef).
    /// </summary>
    public class FirepitRendererAdapter : IStoveTopRenderer
    {
        readonly ICoreClientAPI capi;
        readonly IInFirepitRenderer wrappedRenderer;
        readonly BlockPos stovePos;

        readonly Matrixf modelMat = new Matrixf();

        FieldInfo potMeshField;
        FieldInfo contentMeshField;
        FieldInfo lidMeshField;
        FieldInfo lidOffsetField;
        FieldInfo wobbleAngleField;

        bool meshFieldsResolved;
        bool disposed;

        public FirepitRendererAdapter(ICoreClientAPI capi, IInFirepitRenderer renderer, BlockPos stovePos)
        {
            this.capi = capi;
            this.wrappedRenderer = renderer;
            this.stovePos = stovePos;
        }

        public void OnUpdate(float temperature)
        {
            wrappedRenderer?.OnUpdate(temperature);
        }

        public void OnCookingComplete()
        {
            if (wrappedRenderer is IInFirepitRenderer fp)
            {
                var method = fp.GetType().GetMethod("OnCookingComplete", BindingFlags.Public | BindingFlags.Instance);
                method?.Invoke(fp, null);
            }
        }

        public void Render(BlockPos pos, float partialTicks)
        {
            if (disposed || wrappedRenderer == null) return;

            MeshRef potRef = null;
            MeshRef contentRef = null;
            MeshRef lidRef = null;
            float lidOffsetY = 5.5f / 16f;
            float wobbleAngle = 0;

            if (wrappedRenderer is IProvidesStoveMeshes meshProvider)
            {
                potRef = meshProvider.PotMeshRef;
                contentRef = meshProvider.ContentMeshRef;
                lidRef = meshProvider.LidMeshRef;
                lidOffsetY = meshProvider.LidOffsetY;
                wobbleAngle = meshProvider.LidWobbleAngle;
            }
            else
            {
                ResolveMeshFieldsViaReflection();
                potRef = GetFieldValue<MeshRef>(potMeshField);
                contentRef = GetFieldValue<MeshRef>(contentMeshField);
                lidRef = GetFieldValue<MeshRef>(lidMeshField);
                if (lidOffsetField != null)
                    lidOffsetY = GetFieldValue<float>(lidOffsetField);
                if (wobbleAngleField != null)
                    wobbleAngle = GetFieldValue<float>(wobbleAngleField);
            }

            if (potRef == null) return;

            IRenderAPI rpi = capi.Render;
            Vec3d camPos = capi.World.Player.Entity.CameraPos;

            rpi.GlDisableCullFace();
            rpi.GlToggleBlend(true);

            IStandardShaderProgram prog = rpi.PreparedStandardShader(pos.X, pos.Y, pos.Z);
            prog.Use();
            prog.Tex2D = capi.BlockTextureAtlas.AtlasTextures[0].TextureId;
            prog.DontWarpVertices = 0;
            prog.AddRenderFlags = 0;
            prog.RgbaAmbientIn = rpi.AmbientColor;
            prog.RgbaFogIn = rpi.FogColor;
            prog.FogMinIn = rpi.FogMin;
            prog.FogDensityIn = rpi.FogDensity;
            prog.RgbaTint = ColorUtil.WhiteArgbVec;
            prog.ExtraGlow = 0;

            float heightOffset = 14f / 16f;

            prog.ModelMatrix = modelMat
                .Identity()
                .Translate(pos.X - camPos.X, pos.Y - camPos.Y + heightOffset, pos.Z - camPos.Z)
                .Values;
            prog.ViewMatrix = rpi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;

            rpi.RenderMesh(potRef);

            if (contentRef != null)
            {
                rpi.RenderMesh(contentRef);
            }

            if (lidRef != null)
            {
                float origx = GameMath.Sin(capi.World.ElapsedMilliseconds / 300f) * 5 / 16f;
                float origz = GameMath.Cos(capi.World.ElapsedMilliseconds / 300f) * 5 / 16f;

                prog.ModelMatrix = modelMat
                    .Identity()
                    .Translate(pos.X - camPos.X, pos.Y - camPos.Y + heightOffset, pos.Z - camPos.Z)
                    .Translate(0, lidOffsetY, 0)
                    .Translate(-origx, 0, -origz)
                    .RotateX(wobbleAngle)
                    .RotateZ(wobbleAngle)
                    .Translate(origx, 0, origz)
                    .Values;
                prog.ViewMatrix = rpi.CameraMatrixOriginf;
                prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;

                rpi.RenderMesh(lidRef);
            }

            prog.Stop();
            rpi.GlToggleBlend(false);
            rpi.GlEnableCullFace();
        }

        void ResolveMeshFieldsViaReflection()
        {
            if (meshFieldsResolved) return;
            meshFieldsResolved = true;

            if (wrappedRenderer == null) return;

            var type = wrappedRenderer.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            potMeshField = type.GetField("potMeshRef", flags) 
                        ?? type.GetField("potRef", flags)
                        ?? type.GetField("PotMeshRef", flags);

            contentMeshField = type.GetField("contentMeshRef", flags) 
                            ?? type.GetField("contentRef", flags)
                            ?? type.GetField("mealMeshRef", flags)
                            ?? type.GetField("ContentMeshRef", flags);

            lidMeshField = type.GetField("lidMeshRef", flags) 
                        ?? type.GetField("lidRef", flags)
                        ?? type.GetField("LidMeshRef", flags);

            lidOffsetField = type.GetField("lidOffsetY", flags)
                          ?? type.GetField("LidOffsetY", flags);

            wobbleAngleField = type.GetField("wobbleAngle", flags)
                            ?? type.GetField("lidWobbleAngle", flags)
                            ?? type.GetField("currentWobbleAngle", flags);
        }

        T GetFieldValue<T>(FieldInfo field)
        {
            if (field == null || wrappedRenderer == null) return default;
            try
            {
                return (T)field.GetValue(wrappedRenderer);
            }
            catch
            {
                return default;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (wrappedRenderer is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
