using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using StoveMod.API;

namespace StoveMod
{
    /// <summary>
    /// Client-side renderer that manages and renders custom stove top renderers.
    /// Registered as an IRenderer to draw additional meshes for modded containers.
    /// </summary>
    public class StoveCustomRenderer : IRenderer
    {
        readonly ICoreClientAPI capi;
        readonly BlockPos pos;

        IStoveTopRenderer inputRenderer;
        IStoveTopRenderer outputRenderer;
        
        ItemStack lastInputStack;
        ItemStack lastOutputStack;
        
        bool forceRefresh;
        bool disposed;
        
        float currentTemperature;
        
        public string Orientation = "south";

        public double RenderOrder => 0.51;
        public int RenderRange => 48;

        public StoveCustomRenderer(ICoreClientAPI capi, BlockPos pos)
        {
            this.capi = capi;
            this.pos = pos;
        }

        public void SetTemperature(float temperature)
        {
            currentTemperature = temperature;
            inputRenderer?.OnUpdate(temperature);
            outputRenderer?.OnUpdate(temperature);
        }

        public void OnCookingComplete()
        {
            inputRenderer?.OnCookingComplete();
            outputRenderer?.OnCookingComplete();
        }

        public void UpdateContents(
            ItemStack inputStack,
            ItemStack outputStack,
            BlockEntity stoveBE,
            StoveRendererRegistry registry)
        {
            if (disposed) return;

            if (inputStack != null && inputStack.Collectible == null) inputStack = null;
            if (outputStack != null && outputStack.Collectible == null) outputStack = null;

            bool inputChanged = forceRefresh || !StacksEqual(inputStack, lastInputStack);
            bool outputChanged = forceRefresh || !StacksEqual(outputStack, lastOutputStack);
            
            forceRefresh = false;

            if (inputChanged)
            {
                DisposeInputRenderer();
                lastInputStack = inputStack?.Clone();
                
                if (inputStack != null && !IsVanillaClayPot(inputStack))
                {
                    inputRenderer = registry?.TryCreateRenderer(inputStack, stoveBE, false);
                    inputRenderer?.OnUpdate(currentTemperature);
                }
            }

            if (outputChanged)
            {
                DisposeOutputRenderer();
                lastOutputStack = outputStack?.Clone();
                
                if (outputStack != null && !IsVanillaClayPot(outputStack))
                {
                    outputRenderer = registry?.TryCreateRenderer(outputStack, stoveBE, true);
                    outputRenderer?.OnUpdate(currentTemperature);
                }
            }
        }

        public void ForceRefresh()
        {
            forceRefresh = true;
        }

        public bool HasInputRenderer => inputRenderer != null;
        public bool HasOutputRenderer => outputRenderer != null;

        bool IsVanillaClayPot(ItemStack stack)
        {
            if (stack == null) return false;
            var collectible = stack.Collectible;
            if (collectible == null) return false;
            
            if (!(collectible is Vintagestory.GameContent.BlockCookingContainer) && 
                !(collectible is Vintagestory.GameContent.BlockCookedContainer))
                return false;
            
            if (collectible.Code?.Domain != "game")
                return false;
            
            string path = collectible.Code?.Path ?? "";
            return path.StartsWith("claypot-") || path.StartsWith("bowl-");
        }

        bool StacksEqual(ItemStack a, ItemStack b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            
            if (a.Collectible?.Code?.ToString() != b.Collectible?.Code?.ToString()) return false;
            if (a.StackSize != b.StackSize) return false;
            
            string attrA = a.Attributes?.ToJsonToken()?.ToString() ?? "";
            string attrB = b.Attributes?.ToJsonToken()?.ToString() ?? "";
            if (attrA != attrB) return false;

            
            return true;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (disposed) return;
            
            inputRenderer?.Render(pos, deltaTime);
            outputRenderer?.Render(pos, deltaTime);
        }

        void DisposeInputRenderer()
        {
            try { inputRenderer?.Dispose(); }
            catch { }
            inputRenderer = null;
            lastInputStack = null;
        }

        void DisposeOutputRenderer()
        {
            try { outputRenderer?.Dispose(); }
            catch { }
            outputRenderer = null;
            lastOutputStack = null;
        }

        public void ClearAll()
        {
            DisposeInputRenderer();
            DisposeOutputRenderer();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ClearAll();
        }
    }
}
