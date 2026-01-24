using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace StoveMod
{
    public class BlockStove : Block, IIgnitable
    {
        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityStove stove)
            {
                return stove.OnInteract(byPlayer, blockSel);
            }
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
        {
            if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityStove stove)
            {
                return stove.GetBlockInfo(forPlayer);
            }
            return base.GetPlacedBlockInfo(world, pos, forPlayer);
        }

        public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
        {
            if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityStove stove)
            {
                stove.DropAllContents();
            }
            
            base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
        }

        public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1)
        {
            Block southVariant = GetSouthVariant(world);
            return new ItemStack[] { new ItemStack(southVariant ?? this) };
        }

        public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
        {
            Block southVariant = GetSouthVariant(world);
            return new ItemStack(southVariant ?? this);
        }

        Block GetSouthVariant(IWorldAccessor world)
        {
            string path = Code?.Path;
            if (string.IsNullOrEmpty(path)) return null;

            string[] orientations = new[] { "-north", "-east", "-west" };
            foreach (var orient in orientations)
            {
                if (path.EndsWith(orient))
                {
                    string southPath = path.Substring(0, path.Length - orient.Length) + "-south";
                    return world.GetBlock(new AssetLocation(Code.Domain, southPath));
                }
            }

            return this;
        }

        public EnumIgniteState OnTryIgniteBlock(EntityAgent byEntity, BlockPos pos, float secondsIgniting)
        {
            if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityStove stove)
            {
                return stove.GetIgnitableState(secondsIgniting);
            }
            return EnumIgniteState.NotIgnitablePreventDefault;
        }

        public void OnTryIgniteBlockOver(EntityAgent byEntity, BlockPos pos, float secondsIgniting, ref EnumHandling handling)
        {
            if (secondsIgniting < 3) return;

            handling = EnumHandling.PreventDefault;

            if (api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityStove stove)
            {
                stove.TryIgnite();
            }
        }

        public EnumIgniteState OnTryIgniteStack(EntityAgent byEntity, BlockPos pos, ItemSlot slot, float secondsIgniting)
        {
            return EnumIgniteState.NotIgnitable;
        }
    }
}
