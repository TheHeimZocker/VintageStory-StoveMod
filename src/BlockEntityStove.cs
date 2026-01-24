using System;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using StoveMod.API;

namespace StoveMod
{
    public class BlockEntityStove : BlockEntityOpenableContainer, IHeatSource
    {
        internal InventoryGeneric stoveInventory;
        GuiDialogBlockEntityStove clientDialog;
        
        public float furnaceTemperature = 20;
        float prevFurnaceTemperature = 20;
        public int maxTemperature;
        
        public float inputStackCookingTime;
        
        public float fuelBurnTime;
        public float maxFuelBurnTime;
        
        public bool canIgniteFuel;
        double extinguishedTotalHours;
        
        const float HeatModifier = 1.25f;
        const float BurnDurationModifier = 1.0f;
        
        bool shouldRedraw;
        
        StoveContentsRenderer renderer;
        StoveCustomRenderer customRenderer;
        StoveRendererRegistry rendererRegistry;

        public override InventoryBase Inventory => stoveInventory;
        public override string InventoryClassName => "stove";
        
        public ItemSlot FuelSlot => stoveInventory[0];
        public ItemSlot InputSlot => stoveInventory[1];
        public ItemSlot OutputSlot => stoveInventory[2];
        public ItemSlot[] CookingSlots => new ItemSlot[] { stoveInventory[3], stoveInventory[4], stoveInventory[5], stoveInventory[6] };
        
        public bool IsBurning => fuelBurnTime > 0;
        public bool IsSmoldering => canIgniteFuel && !IsBurning;
        
        public bool HasCookingContainer
        {
            get
            {
                if (stoveInventory == null || stoveInventory[1]?.Itemstack == null) return false;
                var collectible = stoveInventory[1].Itemstack.Collectible;
                return collectible is BlockCookingContainer || 
                       collectible is BlockCookedContainer ||
                       collectible is IInFirepitMeshSupplier;
            }
        }

        public bool HasCookedMealInInput
        {
            get
            {
                if (stoveInventory == null || stoveInventory[1]?.Itemstack == null) return false;
                var collectible = stoveInventory[1].Itemstack.Collectible;
                return collectible is BlockCookedContainer;
            }
        }

        public float BurnTimeRemaining => fuelBurnTime;
        public float MaxBurnTime => maxFuelBurnTime;
        public float CurrentTemperature => furnaceTemperature;
        public bool HasPot => HasCookingContainer;

        const int MaxCookingSlotStackSize = 6;

        public BlockEntityStove()
        {
            stoveInventory = new InventoryGeneric(7, null, null, null);
            ConfigureSlots();
        }

        void ConfigureSlots()
        {
            for (int i = 3; i <= 6; i++)
            {
                stoveInventory[i].MaxSlotStackSize = MaxCookingSlotStackSize;
            }
        }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            
            stoveInventory.LateInitialize(InventoryClassName + "-" + Pos, api);
            stoveInventory.SlotModified += OnSlotModified;
            
            SanitizeInventory();
            
            RegisterGameTickListener(OnBurnTick, 100);
            RegisterGameTickListener(On500msTick, 500);
            
            if (api is ICoreClientAPI capi)
            {
                RegisterGameTickListener(OnClientTick, 50);
                
                renderer = new StoveContentsRenderer(capi, Pos);
                capi.Event.RegisterRenderer(renderer, EnumRenderStage.Opaque, "stove");
                
                customRenderer = new StoveCustomRenderer(capi, Pos);
                capi.Event.RegisterRenderer(customRenderer, EnumRenderStage.Opaque, "stove-custom");
                
                var modSystem = capi.ModLoader.GetModSystem<StoveModSystem>();
                rendererRegistry = modSystem?.RendererRegistry;
                
                UpdateRenderer();
            }
        }

        private void OnSlotModified(int slotId)
        {
            Block = Api.World.BlockAccessor.GetBlock(Pos);
            
            UpdateRenderer();
            MarkDirty(Api.Side == EnumAppSide.Server);
            shouldRedraw = true;
            
            if (Api is ICoreClientAPI && clientDialog != null)
            {
                SetDialogValues(clientDialog.Attributes);
            }
            
            if (slotId == 1 && !HasCookingContainer)
            {
                DropCookingSlots();
            }
            
            Api.World.BlockAccessor.GetChunkAtBlockPos(Pos)?.MarkModified();
        }

        private void DropCookingSlots()
        {
            if (Api?.Side != EnumAppSide.Server) return;
            
            Vec3d dropPos = Pos.ToVec3d().Add(0.5, 1.0, 0.5);
            foreach (var slot in CookingSlots)
            {
                if (!slot.Empty)
                {
                    Api.World.SpawnItemEntity(slot.Itemstack, dropPos);
                    slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }
        }

        private void On500msTick(float dt)
        {
            if (Api.Side == EnumAppSide.Server && (IsBurning || prevFurnaceTemperature != furnaceTemperature))
            {
                MarkDirty();
            }
            prevFurnaceTemperature = furnaceTemperature;
        }

        private void OnBurnTick(float dt)
        {
            if (Api.Side == EnumAppSide.Client)
            {
                renderer?.OnUpdate(InputStackTemp);
                customRenderer?.SetTemperature(InputStackTemp);
                return;
            }

            if (fuelBurnTime > 0)
            {
                bool lowFuelConsumption = Math.Abs(furnaceTemperature - maxTemperature) < 50 && InputSlot.Empty;
                fuelBurnTime -= dt / (lowFuelConsumption ? 1.5f : 1);
                
                if (fuelBurnTime <= 0)
                {
                    fuelBurnTime = 0;
                    maxFuelBurnTime = 0;
                    canIgniteFuel = true;
                    extinguishedTotalHours = Api.World.Calendar.TotalHours;
                }
            }

            if (!IsBurning && canIgniteFuel && Api.World.Calendar.TotalHours - extinguishedTotalHours > 2)
            {
                canIgniteFuel = false;
            }

            if (IsBurning)
            {
                furnaceTemperature = ChangeTemperature(furnaceTemperature, maxTemperature, dt);
            }

            if (CanHeatInput())
            {
                HeatInput(dt);
            }
            else
            {
                inputStackCookingTime = 0;
            }

            if (CanHeatOutput())
            {
                HeatOutput(dt);
            }

            if (CanSmeltInput() && inputStackCookingTime > GetMaxCookingTime())
            {
                DoSmelt();
            }

            if (!IsBurning && canIgniteFuel && CanSmelt())
            {
                IgniteFuel();
            }

            if (!IsBurning)
            {
                furnaceTemperature = ChangeTemperature(furnaceTemperature, GetEnvironmentTemperature(), dt);
            }
        }

        private void OnClientTick(float dt)
        {
            if (clientDialog?.IsOpened() == true)
            {
                SetDialogValues(clientDialog.Attributes);
            }
            
            SpawnSteamParticles();
        }
        
        private void SpawnSteamParticles()
        {
            if (Api.Side != EnumAppSide.Client) return;
            if (OutputSlot.Empty) return;
            
            float outputTemp = OutputStackTemp;
            if (outputTemp < 50) return;
            
            if (Api.World.Rand.NextDouble() > 0.15) return;
            
            ICoreClientAPI capi = Api as ICoreClientAPI;
            
            SimpleParticleProperties steam = new SimpleParticleProperties(
                1, 1,
                ColorUtil.ToRgba(50, 220, 220, 220),
                new Vec3d(),
                new Vec3d(),
                new Vec3f(-0.1f, 0.3f, -0.1f),
                new Vec3f(0.1f, 0.6f, 0.1f),
                1.5f,
                0f,
                0.25f,
                0.75f,
                EnumParticleModel.Quad
            );
            
            steam.MinPos = new Vec3d(Pos.X + 0.3, Pos.Y + 1.2, Pos.Z + 0.3);
            steam.AddPos = new Vec3d(0.4, 0.1, 0.4);
            steam.SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEARINCREASE, 0.5f);
            steam.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEARREDUCE, 150);
            steam.SelfPropelled = true;
            
            capi.World.SpawnParticles(steam);
        }

        public float ChangeTemperature(float fromTemp, float toTemp, float dt)
        {
            float diff = Math.Abs(fromTemp - toTemp);
            dt = dt + dt * (diff / 28);
            
            if (diff < dt) return toTemp;
            if (fromTemp > toTemp) dt = -dt;
            if (Math.Abs(fromTemp - toTemp) < 1) return toTemp;
            
            return fromTemp + dt;
        }

        public float GetEnvironmentTemperature()
        {
            return Api.World.BlockAccessor.GetClimateAt(Pos, EnumGetClimateMode.NowValues)?.Temperature ?? 20;
        }

        public float InputStackTemp
        {
            get => GetTemp(InputSlot.Itemstack);
            set => SetTemp(InputSlot.Itemstack, value);
        }
        
        public float OutputStackTemp
        {
            get => GetTemp(OutputSlot.Itemstack);
            set => SetTemp(OutputSlot.Itemstack, value);
        }

        float GetTemp(ItemStack stack)
        {
            if (stack == null) return GetEnvironmentTemperature();

            if (HasCookingContainer)
            {
                bool haveStack = false;
                float lowestTemp = 0;
                foreach (var slot in CookingSlots)
                {
                    if (!slot.Empty)
                    {
                        float stackTemp = slot.Itemstack.Collectible.GetTemperature(Api.World, slot.Itemstack);
                        lowestTemp = haveStack ? Math.Min(lowestTemp, stackTemp) : stackTemp;
                        haveStack = true;
                    }
                }
                if (haveStack) return lowestTemp;
            }

            return stack.Collectible.GetTemperature(Api.World, stack);
        }

        void SetTemp(ItemStack stack, float value)
        {
            if (stack == null) return;
            
            if (HasCookingContainer)
            {
                foreach (var slot in CookingSlots)
                {
                    slot.Itemstack?.Collectible.SetTemperature(Api.World, slot.Itemstack, value);
                }
            }
            else
            {
                stack.Collectible.SetTemperature(Api.World, stack, value);
            }
        }

        private bool CanSmelt()
        {
            if (FuelSlot.Empty) return false;
            
            if (!IsValidFuel(FuelSlot.Itemstack)) return false;
            
            var fuelProps = FuelSlot.Itemstack?.Collectible?.CombustibleProps;
            if (fuelProps == null) return false;
            
            return fuelProps.BurnTemperature * HeatModifier > 0;
        }

        private bool IsValidFuel(ItemStack stack)
        {
            if (stack == null) return false;
            string path = stack.Collectible?.Code?.Path ?? "";
            return path.StartsWith("charcoal") || 
                   path.StartsWith("anthracite") || 
                   path.StartsWith("coal") ||
                   path.StartsWith("coke") ||
                   path == "ore-anthracite" ||
                   path == "ore-bituminouscoal";
        }

        private bool CanHeatInput()
        {
            return CanSmeltInput() || 
                   (InputSlot.Itemstack?.ItemAttributes?["allowHeating"]?.AsBool() == true);
        }

        private bool CanHeatOutput()
        {
            return OutputSlot.Itemstack?.ItemAttributes?["allowHeating"]?.AsBool() == true;
        }

        private bool CanSmeltInput()
        {
            if (InputSlot.Empty) return false;
            
            var collectible = InputSlot.Itemstack.Collectible;
            
            if (collectible is BlockCookingContainer || collectible is BlockCookedContainer)
            {
                if (collectible is BlockCookedContainer) return false;
                
                foreach (var slot in CookingSlots)
                {
                    if (!slot.Empty) return true;
                }
                return false;
            }
            
            var combustProps = collectible.CombustibleProps;
            if (combustProps == null) return false;
            if (combustProps.RequiresContainer) return false;
            if (combustProps.SmeltedStack == null) return false;
            
            return true;
        }

        private float GetMaxCookingTime()
        {
            if (InputSlot.Empty) return 30f;
            
            var collectible = InputSlot.Itemstack.Collectible;
            
            if (collectible is BlockCookingContainer || collectible is BlockCookedContainer)
            {
                return collectible.CombustibleProps?.MeltingDuration ?? 30f;
            }
            
            return collectible.CombustibleProps?.MeltingDuration ?? 30f;
        }

        private void HeatInput(float dt)
        {
            var inputStack = InputSlot.Itemstack;
            if (inputStack == null) return;

            float oldTemp = InputStackTemp;
            float nowTemp = oldTemp;
            float meltingPoint = inputStack.Collectible.CombustibleProps?.MeltingPoint ?? 100;

            if (oldTemp < furnaceTemperature)
            {
                float f = (1 + GameMath.Clamp((furnaceTemperature - oldTemp) / 30, 0, 1.6f)) * dt;
                if (nowTemp >= meltingPoint) f /= 11;
                
                float newTemp = ChangeTemperature(oldTemp, furnaceTemperature, f);
                
                int maxTemp = Math.Max(
                    inputStack.Collectible.CombustibleProps?.MaxTemperature ?? 0,
                    inputStack.ItemAttributes?["maxTemperature"]?.AsInt(0) ?? 0
                );
                if (maxTemp > 0) newTemp = Math.Min(maxTemp, newTemp);
                
                if (oldTemp != newTemp)
                {
                    InputStackTemp = newTemp;
                    nowTemp = newTemp;
                }
            }

            float minCookingTemp = HasCookingContainer ? 150f : meltingPoint;
            if (nowTemp >= minCookingTemp)
            {
                bool hasValidRecipe = true;
                if (HasCookingContainer)
                {
                    ItemStack[] stacks = GetCookingStacks();
                    var recipes = Api.GetCookingRecipes();
                    hasValidRecipe = recipes?.FirstOrDefault(r => r.Matches(stacks)) != null;
                }
                
                if (hasValidRecipe)
                {
                    float diff = nowTemp / minCookingTemp;
                    inputStackCookingTime += GameMath.Clamp((int)diff, 1, 30) * dt;
                }
            }
            else
            {
                if (inputStackCookingTime > 0) inputStackCookingTime--;
            }
        }

        private void HeatOutput(float dt)
        {
            var outputStack = OutputSlot.Itemstack;
            if (outputStack == null) return;

            float oldTemp = OutputStackTemp;
            
            if (oldTemp < furnaceTemperature)
            {
                float newTemp = ChangeTemperature(oldTemp, furnaceTemperature, 2 * dt);
                
                int maxTemp = Math.Max(
                    outputStack.Collectible.CombustibleProps?.MaxTemperature ?? 0,
                    outputStack.ItemAttributes?["maxTemperature"]?.AsInt(0) ?? 0
                );
                if (maxTemp > 0) newTemp = Math.Min(maxTemp, newTemp);
                
                if (oldTemp != newTemp)
                {
                    OutputStackTemp = newTemp;
                }
            }
        }

        private void DoSmelt()
        {
            var inputStack = InputSlot.Itemstack;
            if (inputStack == null) return;

            var collectible = inputStack.Collectible;

            if (collectible is BlockCookingContainer)
            {
                ItemStack[] stacks = GetCookingStacks();
                var recipes = Api.GetCookingRecipes();
                if (recipes != null)
                {
                    CookingRecipe recipe = recipes.FirstOrDefault(r => r.Matches(stacks));
                    if (recipe != null)
                    {
                        Block cookedBlock = Api.World.GetBlock(collectible.CodeWithVariant("type", "cooked"));
                        if (cookedBlock != null && cookedBlock is BlockCookedContainerBase cookedContainer)
                        {
                            ItemStack cookedStack = new ItemStack(cookedBlock);
                            
                            float quantityServings = recipe.GetQuantityServings(stacks);
                            
                            cookedContainer.SetContents(recipe.Code, quantityServings, cookedStack, stacks);
                            
                            cookedBlock.SetTemperature(Api.World, cookedStack, InputStackTemp);
                            
                            foreach (var slot in CookingSlots)
                            {
                                slot.Itemstack = null;
                                slot.MarkDirty();
                            }
                            
                            InputSlot.Itemstack = null;
                            InputSlot.MarkDirty();
                            
                            OutputSlot.Itemstack = cookedStack;
                            OutputSlot.MarkDirty();
                            
                            if (Api.Side == EnumAppSide.Client)
                            {
                                renderer?.OnCookingComplete();
                                customRenderer?.OnCookingComplete();
                            }
                        }
                    }
                }
            }
            else
            {
                var combustProps = collectible.CombustibleProps;
                if (combustProps?.SmeltedStack != null)
                {
                    ItemStack smeltedStack = combustProps.SmeltedStack.ResolvedItemstack?.Clone();
                    if (smeltedStack != null)
                    {
                        smeltedStack.StackSize *= inputStack.StackSize / combustProps.SmeltedRatio;
                        
                        if (OutputSlot.Empty)
                        {
                            OutputSlot.Itemstack = smeltedStack;
                        }
                        else if (OutputSlot.Itemstack.Equals(Api.World, smeltedStack, GlobalConstants.IgnoredStackAttributes))
                        {
                            int space = OutputSlot.Itemstack.Collectible.MaxStackSize - OutputSlot.StackSize;
                            int transfer = Math.Min(space, smeltedStack.StackSize);
                            OutputSlot.Itemstack.StackSize += transfer;
                        }
                        OutputSlot.MarkDirty();
                        
                        InputSlot.Itemstack = null;
                        InputSlot.MarkDirty();
                    }
                }
            }
            
            InputStackTemp = GetEnvironmentTemperature();
            inputStackCookingTime = 0;
            MarkDirty(true);
        }

        public void IgniteFuel()
        {
            if (FuelSlot.Empty) return;
            
            if (!IsValidFuel(FuelSlot.Itemstack)) return;
            
            var fuelProps = FuelSlot.Itemstack.Collectible.CombustibleProps;
            if (fuelProps == null) return;
            
            string fuelPath = FuelSlot.Itemstack.Collectible.Code.Path;
            isCharcoalFuel = fuelPath.StartsWith("charcoal");
            isCokeFuel = fuelPath.StartsWith("coke");
            
            maxFuelBurnTime = fuelBurnTime = fuelProps.BurnDuration * BurnDurationModifier;
            maxTemperature = (int)(fuelProps.BurnTemperature * HeatModifier);
            
            FuelSlot.TakeOut(1);
            FuelSlot.MarkDirty();
            MarkDirty(true);
        }

        bool isCharcoalFuel = false;
        bool isCokeFuel = false;

        int GetMaxDisplayTemperature()
        {
            if (isCokeFuel) return 1340;
            if (isCharcoalFuel) return 1300;
            return 1200;
        }

        void SetDialogValues(ITreeAttribute dialogTree)
        {
            int maxDisplayTemp = GetMaxDisplayTemperature();
            float displayTemp = Math.Min(furnaceTemperature, maxDisplayTemp);
            int displayMaxTemp = Math.Min(maxTemperature, maxDisplayTemp);
            
            dialogTree.SetFloat("furnaceTemperature", displayTemp);
            dialogTree.SetInt("maxTemperature", displayMaxTemp);
            dialogTree.SetFloat("oreCookingTime", inputStackCookingTime);
            dialogTree.SetFloat("maxFuelBurnTime", maxFuelBurnTime);
            dialogTree.SetFloat("fuelBurnTime", fuelBurnTime);
            
            if (InputSlot.Itemstack != null)
            {
                float meltingDuration = InputSlot.Itemstack.Collectible.CombustibleProps?.MeltingDuration ?? 30f;
                dialogTree.SetFloat("oreTemperature", InputStackTemp);
                dialogTree.SetFloat("maxOreCookingTime", meltingDuration);
            }
            else
            {
                dialogTree.RemoveAttribute("oreTemperature");
            }
            
            dialogTree.SetString("outputText", GetOutputText());
            dialogTree.SetInt("haveCookingContainer", HasCookingContainer ? 1 : 0);
            dialogTree.SetInt("quantityCookingSlots", HasCookingContainer ? 4 : 0);
            
            bool hasValidRecipe = false;
            if (HasCookingContainer)
            {
                ItemStack[] stacks = GetCookingStacks();
                var recipes = Api.GetCookingRecipes();
                hasValidRecipe = recipes?.FirstOrDefault(r => r.Matches(stacks)) != null;
            }
            dialogTree.SetInt("hasValidRecipe", hasValidRecipe ? 1 : 0);
        }
        
        string GetOutputText()
        {
            if (HasCookingContainer)
            {
                if (HasCookedMealInInput)
                {
                    return "";
                }
                
                ItemStack[] stacks = GetCookingStacks();
                
                bool hasItems = false;
                foreach (var stack in stacks)
                {
                    if (stack != null) { hasItems = true; break; }
                }
                
                if (!hasItems) return "";
                
                var recipes = Api.GetCookingRecipes();
                if (recipes != null)
                {
                    CookingRecipe recipe = recipes.FirstOrDefault(r => r.Matches(stacks));
                    if (recipe != null)
                    {
                        int servings = recipe.GetQuantityServings(stacks);
                        string recipeName = recipe.GetOutputName(Api.World, stacks);
                        return $"Will create {servings} servings of {recipeName}";
                    }
                }
                
                return "No matching recipe found";
            }
            return "";
        }
        
        ItemStack[] GetCookingStacks()
        {
            ItemStack[] stacks = new ItemStack[4];
            for (int i = 0; i < 4; i++)
            {
                stacks[i] = CookingSlots[i].Itemstack;
            }
            return stacks;
        }

        public override bool OnPlayerRightClick(IPlayer byPlayer, BlockSelection blockSel)
        {
            if (Api.Side == EnumAppSide.Client)
            {
                ToggleInventoryDialog(byPlayer);
            }
            return true;
        }

        public bool OnInteract(IPlayer byPlayer, BlockSelection blockSel)
        {
            ItemSlot hotbarSlot = byPlayer.InventoryManager.ActiveHotbarSlot;
            bool sneaking = byPlayer.Entity.Controls.ShiftKey;
            
            if (sneaking)
            {
                if (!hotbarSlot.Empty)
                {
                    var collectible = hotbarSlot.Itemstack.Collectible;
                    
                    if (collectible is BlockCookingContainer || collectible is BlockCookedContainer ||
                        collectible is IInFirepitMeshSupplier)
                    {
                        if (InputSlot.Empty)
                        {
                            InputSlot.Itemstack = hotbarSlot.TakeOut(1);
                            InputSlot.MarkDirty();
                            hotbarSlot.MarkDirty();
                            MarkDirty(true);
                            return true;
                        }
                    }
                    
                    if (collectible.CombustibleProps?.BurnTemperature > 0 && collectible.CombustibleProps?.BurnDuration > 0)
                    {
                        int moved = hotbarSlot.TryPutInto(Api.World, FuelSlot, 1);
                        if (moved > 0)
                        {
                            hotbarSlot.MarkDirty();
                            MarkDirty(true);
                            return true;
                        }
                    }
                }
                else
                {
                    if (!OutputSlot.Empty)
                    {
                        if (!IsStackValid(OutputSlot.Itemstack))
                        {
                            Api?.Logger?.Warning("[Stove] Purged invalid itemstack from output slot at " + Pos + " during pickup");
                            OutputSlot.Itemstack = null;
                            OutputSlot.MarkDirty();
                            MarkDirty(true);
                            return true;
                        }
                        
                        if (OutputSlot.Itemstack.Collectible is BlockCookingContainer ||
                            OutputSlot.Itemstack.Collectible is BlockCookedContainer ||
                            OutputSlot.Itemstack.Collectible is IInFirepitMeshSupplier)
                        {
                            if (Api.Side == EnumAppSide.Client)
                            {
                                renderer?.ClearContents();
                                customRenderer?.ClearAll();
                            }
                            
                            if (byPlayer.InventoryManager.TryGiveItemstack(OutputSlot.Itemstack.Clone()))
                            {
                                OutputSlot.Itemstack = null;
                                OutputSlot.MarkDirty();
                                MarkDirty(true);
                                return true;
                            }
                        }
                    }
                    else if (!InputSlot.Empty)
                    {
                        if (!IsStackValid(InputSlot.Itemstack))
                        {
                            Api?.Logger?.Warning("[Stove] Purged invalid itemstack from input slot at " + Pos + " during pickup");
                            InputSlot.Itemstack = null;
                            InputSlot.MarkDirty();
                            MarkDirty(true);
                            return true;
                        }
                        
                        if (InputSlot.Itemstack.Collectible is BlockCookingContainer ||
                            InputSlot.Itemstack.Collectible is BlockCookedContainer ||
                            InputSlot.Itemstack.Collectible is IInFirepitMeshSupplier)
                        {
                            if (Api.Side == EnumAppSide.Client)
                            {
                                renderer?.ClearContents();
                                customRenderer?.ClearAll();
                            }
                            
                            DropCookingSlots();
                            if (byPlayer.InventoryManager.TryGiveItemstack(InputSlot.Itemstack.Clone()))
                            {
                                InputSlot.Itemstack = null;
                                InputSlot.MarkDirty();
                                MarkDirty(true);
                                return true;
                            }
                        }
                    }
                }
                return false;
            }
            
            if (Api.Side == EnumAppSide.Client)
            {
                ToggleInventoryDialog(byPlayer);
            }
            
            return true;
        }

        private void ToggleInventoryDialog(IPlayer byPlayer)
        {
            if (Api.Side != EnumAppSide.Client) return;
            
            var capi = Api as ICoreClientAPI;
            
            if (clientDialog == null)
            {
                SyncedTreeAttribute dtree = new SyncedTreeAttribute();
                SetDialogValues(dtree);
                
                clientDialog = new GuiDialogBlockEntityStove("Stove", stoveInventory, Pos, dtree, capi, this);
                
                clientDialog.OnClosed += () =>
                {
                    clientDialog = null;
                    capi.Network.SendBlockEntityPacket(Pos, (int)EnumBlockEntityPacketId.Close, null);
                };
            }
            
            if (clientDialog.IsOpened())
            {
                clientDialog.TryClose();
            }
            else
            {
                clientDialog.TryOpen();
                capi.Network.SendPacketClient(stoveInventory.Open(byPlayer));
                capi.Network.SendBlockEntityPacket(Pos, (int)EnumBlockEntityPacketId.Open, null);
            }
        }

        public void TryIgnite()
        {
            if (!FuelSlot.Empty && !IsBurning && IsValidFuel(FuelSlot.Itemstack))
            {
                IgniteFuel();
                canIgniteFuel = true;
                Api.World.PlaySoundAt(new AssetLocation("sounds/effect/fire"), Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16);
            }
        }

        public EnumIgniteState GetIgnitableState(float secondsIgniting)
        {
            if (FuelSlot.Empty) return EnumIgniteState.NotIgnitablePreventDefault;
            if (IsBurning) return EnumIgniteState.NotIgnitablePreventDefault;
            return secondsIgniting > 3 ? EnumIgniteState.IgniteNow : EnumIgniteState.Ignitable;
        }

        public override void OnReceivedClientPacket(IPlayer fromPlayer, int packetid, byte[] data)
        {
            if (packetid < 1000)
            {
                stoveInventory.InvNetworkUtil.HandleClientPacket(fromPlayer, packetid, data);
            }

            if (packetid == (int)EnumBlockEntityPacketId.Close)
            {
                fromPlayer.InventoryManager?.CloseInventory(Inventory);
            }
            
            if (packetid == (int)EnumBlockEntityPacketId.Open)
            {
                fromPlayer.InventoryManager?.OpenInventory(Inventory);
            }
            
            Api.World.BlockAccessor.GetChunkAtBlockPos(Pos)?.MarkModified();
        }

        public override void OnReceivedServerPacket(int packetid, byte[] data)
        {
            base.OnReceivedServerPacket(packetid, data);

            if (packetid == (int)EnumBlockEntityPacketId.Close)
            {
                (Api.World as IClientWorldAccessor).Player.InventoryManager.CloseInventory(Inventory);
                clientDialog?.TryClose();
                clientDialog = null;
            }
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            ItemStack inputStack = InputSlot.Itemstack;
            ItemStack outputStack = OutputSlot.Itemstack;
            ItemStack contentStack = inputStack ?? outputStack;
            
            if (contentStack != null)
            {
                if (contentStack.Collectible == null)
                {
                    return false;
                }
                
                bool isCookingContainer = contentStack.Collectible is BlockCookingContainer || 
                                           contentStack.Collectible is BlockCookedContainer;
                
                if (isCookingContainer && ShouldUseCustomPotRenderer(contentStack))
                {
                    return false;
                }
                
                bool isInput = contentStack == inputStack;
                bool hasCustomRenderer = isInput ? 
                    (customRenderer?.HasInputRenderer == true) : 
                    (customRenderer?.HasOutputRenderer == true);
                
                if (hasCustomRenderer)
                {
                    return false;
                }
                
                MeshData mesh = GetContentMesh(contentStack, tesselator);
                if (mesh != null)
                {
                    mesher.AddMeshData(mesh);
                }
            }
            
            return false;
        }

        private MeshData GetContentMesh(ItemStack contentStack, ITesselatorAPI tesselator)
        {
            if (contentStack == null || contentStack.Collectible == null) return null;

            if (contentStack.Collectible is IInFirepitMeshSupplier meshSupplier)
            {
                EnumFirepitModel model = EnumFirepitModel.Normal;
                MeshData mesh = meshSupplier.GetMeshWhenInFirepit(contentStack, Api.World, Pos, ref model);
                if (mesh != null)
                {
                    mesh = mesh.Clone();
                    mesh.Translate(0, 14f / 16f, 0);
                    return mesh;
                }
            }
            
            if (contentStack.Class == EnumItemClass.Block && contentStack.Block != null)
            {
                MeshData mesh = ((ICoreClientAPI)Api).TesselatorManager.GetDefaultBlockMesh(contentStack.Block)?.Clone();
                if (mesh != null)
                {
                    mesh.Translate(0, 14f / 16f, 0);
                    return mesh;
                }
            }
            
            return null;
        }

        bool IsVanillaClayPot(ItemStack stack)
        {
            if (stack == null) return false;
            var collectible = stack.Collectible;
            if (collectible == null) return false;
            
            if (!(collectible is BlockCookingContainer) && !(collectible is BlockCookedContainer))
                return false;
            
            if (collectible.Code?.Domain != "game")
                return false;
            
            string path = collectible.Code?.Path ?? "";
            return path.StartsWith("claypot-") || path.StartsWith("bowl-");
        }

        bool ShouldUseCustomPotRenderer(ItemStack stack)
        {
            if (stack == null) return false;
            
            if (stack.Collectible?.Attributes?["stove"]?["renderMode"]?.AsString() == "claypot")
                return true;
            
            return IsVanillaClayPot(stack);
        }

        void UpdateRenderer()
        {
            if (Api?.Side != EnumAppSide.Client) return;

            string orientation = Block?.Variant?["horizontalorientation"] ?? "south";
            
            if (renderer != null)
            {
                renderer.Orientation = orientation;
            }
            if (customRenderer != null)
            {
                customRenderer.Orientation = orientation;
            }

            ItemStack inputStack = InputSlot.Itemstack;
            ItemStack outputStack = OutputSlot.Itemstack;
            ItemStack contentStack = inputStack ?? outputStack;
            bool isInOutputSlot = contentStack == outputStack && contentStack != null;

            bool isCookingContainer = contentStack?.Collectible is BlockCookingContainer || 
                                       contentStack?.Collectible is BlockCookedContainer;
            bool shouldUseVanillaRenderer = isCookingContainer && ShouldUseCustomPotRenderer(contentStack);

            bool useOldRenderer =
                renderer?.ContentStack != null &&
                contentStack != null &&
                shouldUseVanillaRenderer &&
                renderer.ContentStack.Equals(Api.World, contentStack, GlobalConstants.IgnoredStackAttributes);

            if (!useOldRenderer)
            {
                renderer?.ClearContents();

                if (shouldUseVanillaRenderer)
                {
                    renderer?.SetContents(contentStack, isInOutputSlot);
                }
            }

            customRenderer?.UpdateContents(inputStack, outputStack, this, rendererRegistry);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("furnaceTemperature", furnaceTemperature);
            tree.SetInt("maxTemperature", maxTemperature);
            tree.SetFloat("oreCookingTime", inputStackCookingTime);
            tree.SetFloat("fuelBurnTime", fuelBurnTime);
            tree.SetFloat("maxFuelBurnTime", maxFuelBurnTime);
            tree.SetDouble("extinguishedTotalHours", extinguishedTotalHours);
            tree.SetBool("canIgniteFuel", canIgniteFuel);
            tree.SetBool("isCharcoalFuel", isCharcoalFuel);
            tree.SetBool("isCokeFuel", isCokeFuel);
            tree.SetInt("haveCookingContainer", HasCookingContainer ? 1 : 0);
            tree.SetInt("quantityCookingSlots", HasCookingContainer ? 4 : 0);
            
            ITreeAttribute invtree = new TreeAttribute();
            stoveInventory.ToTreeAttributes(invtree);
            tree["inventory"] = invtree;
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            
            furnaceTemperature = tree.GetFloat("furnaceTemperature");
            maxTemperature = tree.GetInt("maxTemperature");
            inputStackCookingTime = tree.GetFloat("oreCookingTime");
            fuelBurnTime = tree.GetFloat("fuelBurnTime");
            maxFuelBurnTime = tree.GetFloat("maxFuelBurnTime");
            extinguishedTotalHours = tree.GetDouble("extinguishedTotalHours");
            canIgniteFuel = tree.GetBool("canIgniteFuel", true);
            isCharcoalFuel = tree.GetBool("isCharcoalFuel", false);
            isCokeFuel = tree.GetBool("isCokeFuel", false);
            
            if (stoveInventory == null)
            {
                stoveInventory = new InventoryGeneric(7, null, null);
                ConfigureSlots();
            }
            
            stoveInventory.FromTreeAttributes(tree.GetTreeAttribute("inventory"));
            
            if (Api != null)
            {
                stoveInventory.Api = Api;
                stoveInventory.ResolveBlocksOrItems();
                SanitizeInventory();
            }
            
            if (Api?.Side == EnumAppSide.Client)
            {
                UpdateRenderer();
                
                if (clientDialog != null)
                {
                    SetDialogValues(clientDialog.Attributes);
                }
                
                if (shouldRedraw)
                {
                    MarkDirty(true);
                    shouldRedraw = false;
                }
            }
        }

        private string GetRimMaterialName()
        {
            string code = Block?.Code?.Path ?? "";
            string[] parts = code.Split('-');
            if (parts.Length >= 2)
            {
                string rimType = parts[1];
                if (!string.IsNullOrEmpty(rimType))
                {
                    return char.ToUpper(rimType[0]) + rimType.Substring(1) + " rock";
                }
            }
            return "Stone";
        }

        public string GetBlockInfo(IPlayer forPlayer)
        {
            StringBuilder sb = new StringBuilder();
            
            int displayTemp = (int)Math.Min(furnaceTemperature, GetMaxDisplayTemperature());
            
            string state = IsBurning ? "(Lit)" : "(Cold)";
            sb.AppendLine(state);
            
            if (IsBurning || furnaceTemperature > 25)
            {
                sb.AppendLine($"Temperature: {displayTemp}°C");
            }
            
            if (!FuelSlot.Empty)
            {
                sb.AppendLine(Lang.Get("Fuel") + ": " + FuelSlot.Itemstack.GetName() + " x" + FuelSlot.StackSize);
            }
            
            if (HasCookingContainer && !HasCookedMealInInput)
            {
                string recipeText = GetOutputText();
                if (IsBurning && !string.IsNullOrEmpty(recipeText) && !recipeText.Contains("No matching"))
                {
                    string recipeName = recipeText;
                    if (recipeText.Contains(" of "))
                    {
                        recipeName = recipeText.Substring(recipeText.LastIndexOf(" of ") + 4);
                    }
                    sb.AppendLine($"Cooking {recipeName}");
                }
                else if (!string.IsNullOrEmpty(recipeText))
                {
                    sb.AppendLine(recipeText);
                }
            }
            
            sb.AppendLine("Stored food perish speed: 0.75x");
            
            return sb.ToString();
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            dsc.Append(GetBlockInfo(forPlayer));
        }

        public void DropAllContents()
        {
            if (Api?.World == null) return;
            
            if (Api.Side == EnumAppSide.Client)
            {
                renderer?.ClearContents();
                customRenderer?.ClearAll();
            }
            
            for (int i = 0; i < stoveInventory.Count; i++)
            {
                ItemSlot slot = stoveInventory[i];
                if (!slot.Empty)
                {
                    if (slot.Itemstack?.Collectible != null)
                    {
                        Api.World.SpawnItemEntity(slot.Itemstack, Pos.ToVec3d().Add(0.5, 0.5, 0.5));
                    }
                    slot.Itemstack = null;
                    slot.MarkDirty();
                }
            }
        }

        public override void OnBlockRemoved()
        {
            if (Api?.Side == EnumAppSide.Client)
            {
                renderer?.ClearContents();
                customRenderer?.ClearAll();
            }
            
            base.OnBlockRemoved();
            clientDialog?.TryClose();
            clientDialog?.Dispose();
            renderer?.Dispose();
            renderer = null;
            customRenderer?.Dispose();
            customRenderer = null;
        }

        public override void OnBlockUnloaded()
        {
            if (Api?.Side == EnumAppSide.Client)
            {
                renderer?.ClearContents();
                customRenderer?.ClearAll();
            }
            
            base.OnBlockUnloaded();
            clientDialog?.TryClose();
            renderer?.Dispose();
            renderer = null;
            customRenderer?.Dispose();
            customRenderer = null;
        }

        void SanitizeInventory()
        {
            if (stoveInventory == null) return;
            
            bool purgedAny = false;
            
            for (int i = 0; i < stoveInventory.Count; i++)
            {
                ItemSlot slot = stoveInventory[i];
                if (slot?.Itemstack != null && !IsStackValid(slot.Itemstack))
                {
                    string code = slot.Itemstack.Collectible?.Code?.ToString() ?? "unknown";
                    Api?.Logger?.Warning($"[Stove] Purged invalid itemstack from stove at {Pos} slot {i} (missing collectible: {code})");
                    slot.Itemstack = null;
                    slot.MarkDirty();
                    purgedAny = true;
                }
            }
            
            if (purgedAny)
            {
                MarkDirty(true);
            }
        }

        bool IsStackValid(ItemStack stack)
        {
            if (stack == null) return true;
            if (stack.Collectible == null) return false;
            if (stack.Collectible.Code == null) return false;
            return true;
        }

        public float GetHeatStrength(IWorldAccessor world, BlockPos heatSourcePos, BlockPos heatReceiverPos)
        {
            return IsBurning ? 10 : (IsSmoldering ? 0.25f : 0);
        }
    }
}
