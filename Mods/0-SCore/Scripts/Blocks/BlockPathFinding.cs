﻿using Audio;
using Platform;
using UnityEngine;

internal class BlockPathFinding : BlockPlayerSign
{
    private readonly BlockActivationCommand[] cmds =
    {
        new BlockActivationCommand("edit", "pen", false),
        new BlockActivationCommand("lock", "lock", false),
        new BlockActivationCommand("unlock", "unlock", false),
        new BlockActivationCommand("keypad", "keypad", false),
        new BlockActivationCommand("take", "hand", false)
    };


    // Do a pre-check on permissions to remove the ghost "Press <e> to interact" when there's no options.
    public override bool HasBlockActivationCommands(WorldBase _world, BlockValue _blockValue, int _clrIdx, Vector3i _blockPos,
        EntityAlive _entityFocusing)
    {
        var tileEntitySign = (TileEntitySign)_world.GetTileEntity(_clrIdx, _blockPos);
        if (tileEntitySign == null) return false;
    
        if (_world.IsEditor()) return true;
    
        var internalLocalUserIdentifier = PlatformManager.InternalLocalUserIdentifier;
        var isOwner = tileEntitySign.LocalPlayerIsOwner();
        return tileEntitySign.IsUserAllowed(internalLocalUserIdentifier) || isOwner;
    }

    public override BlockActivationCommand[] GetBlockActivationCommands(WorldBase _world, BlockValue _blockValue, int _clrIdx, Vector3i _blockPos, EntityAlive _entityFocusing)
    {
        var tileEntitySign = (TileEntitySign)_world.GetTileEntity(_clrIdx, _blockPos);
        if (tileEntitySign == null) return new BlockActivationCommand[0];

        var internalLocalUserIdentifier = PlatformManager.InternalLocalUserIdentifier;
        var isOwner = tileEntitySign.LocalPlayerIsOwner();

        cmds[0].enabled = _world.IsEditor() || tileEntitySign.IsUserAllowed(internalLocalUserIdentifier) || isOwner; 
        cmds[1].enabled = !tileEntitySign.IsLocked() && isOwner;
        cmds[2].enabled = tileEntitySign.IsLocked() && isOwner;
        cmds[3].enabled = tileEntitySign.IsUserAllowed(internalLocalUserIdentifier) || isOwner;
        cmds[4].enabled = tileEntitySign.IsUserAllowed(internalLocalUserIdentifier) || isOwner;

        return cmds;
    }


    public override bool OnBlockActivated(string commandName, WorldBase _world, int _cIdx,
        Vector3i _blockPos, BlockValue _blockValue, EntityPlayerLocal _player)
    {
        if (_blockValue.ischild)
        {
            var parentPos = list[_blockValue.type].multiBlockPos.GetParentPos(_blockPos, _blockValue);
            var block = _world.GetBlock(parentPos);
            return OnBlockActivated(commandName, _world, _cIdx, parentPos, block, _player);
        }

        if (_world.GetTileEntity(_cIdx, _blockPos) is not TileEntitySign tileEntitySign) return false;

        var internalLocalUserIdentifier = PlatformManager.InternalLocalUserIdentifier;
        switch (commandName)
        {
            case "edit":
                if (GameManager.Instance.IsEditMode() || !tileEntitySign.IsLocked() || tileEntitySign.IsUserAllowed(internalLocalUserIdentifier))
                    return OnBlockActivated(_world, _cIdx, _blockPos, _blockValue, _player);
                Manager.BroadcastPlayByLocalPlayer(_blockPos.ToVector3() + Vector3.one * 0.5f, "Misc/locked");
                return false;
            case "lock":
                tileEntitySign.SetLocked(true);
                Manager.BroadcastPlayByLocalPlayer(_blockPos.ToVector3() + Vector3.one * 0.5f, "Misc/locking");
                GameManager.ShowTooltip(_player as EntityPlayerLocal, "containerLocked");
                return true;
            case "unlock":
                tileEntitySign.SetLocked(false);
                Manager.BroadcastPlayByLocalPlayer(_blockPos.ToVector3() + Vector3.one * 0.5f, "Misc/unlocking");
                GameManager.ShowTooltip(_player as EntityPlayerLocal, "containerUnlocked");
                return true;
            case "keypad":
                XUiC_KeypadWindow.Open(LocalPlayerUI.GetUIForPlayer(_player as EntityPlayerLocal), tileEntitySign);
                return true;
            case "take":
                var uiforPlayer = LocalPlayerUI.GetUIForPlayer(_player as EntityPlayerLocal);
                var itemStack = new ItemStack(_blockValue.ToItemValue(), 1);
                if (!uiforPlayer.xui.PlayerInventory.AddItem(itemStack)) uiforPlayer.xui.PlayerInventory.DropItem(itemStack);
                _world.SetBlockRPC(_cIdx, _blockPos, BlockValue.Air);

                return true;
            default:
                return false;
        }
    }


    public override void OnBlockEntityTransformAfterActivated(WorldBase _world, Vector3i _blockPos, int _cIdx, BlockValue _blockValue, BlockEntityData _ebcd)
    {
        if (_ebcd == null)
            return;
        var chunk = (Chunk)((World)_world).GetChunkFromWorldPos(_blockPos);
        var tileEntitySign = (TileEntitySign)_world.GetTileEntity(_cIdx, _blockPos);
        if (tileEntitySign == null)
        {
            tileEntitySign = new TileEntitySign(chunk) {
                localChunkPos = World.toBlock(_blockPos)
            };
            chunk.AddTileEntity(tileEntitySign);
        }
        
        tileEntitySign.textMesh = _ebcd.transform.GetComponentInChildren<TextMesh>();
        if (tileEntitySign.textMesh == null)
        {
            Debug.Log("TextMesh is null. Adding one.");
            tileEntitySign.textMesh= _ebcd.transform.gameObject.AddComponent<TextMesh>();
            if ( tileEntitySign.textMesh == null )
                Debug.Log("Text Mesh is still null.");
        }
        
        tileEntitySign.smartTextMesh =  tileEntitySign.textMesh.transform.gameObject.AddComponent<SmartTextMesh>();
        var num = (float)_ebcd.blockValue.Block.multiBlockPos.dim.x;
        tileEntitySign.smartTextMesh.MaxWidth = 0.48f * num;
        tileEntitySign.smartTextMesh.MaxLines = this.lineCount;
        tileEntitySign.smartTextMesh.ConvertNewLines = true;
        var authoredText = tileEntitySign.signText;
        tileEntitySign.RefreshTextMesh(authoredText?.Text);
      
        // // Hide the sign, so its not visible. Without this, it errors out.
        _ebcd.bHasTransform = false;
        base.OnBlockEntityTransformAfterActivated(_world, _blockPos, _cIdx, _blockValue, _ebcd);

        // Re-show the transform. This won't have a visual effect, but fixes when you pick up the block, the outline of the block persists.
        _ebcd.bHasTransform = true;
    }
}