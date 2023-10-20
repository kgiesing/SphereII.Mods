using ExtUtilsForEnt;
using GamePath;
using System;
using UnityEngine;
using static EntityDrone;

namespace UAI
{
    /// <summary>
    /// This is new implementation of a task to find and move to cover.
    /// It is unrelated to the TFP code.
    /// </summary>
    public class UAITaskMoveToCover : UAITaskBase
    {
        /// <summary>
        /// The minimum distance, from the checking entity, that should be considered as cover.
        /// </summary>
        private float minSelfDist = 0;

        /// <summary>
        /// The maximum distance, from the checking entity, that should be considered as cover.
        /// A value of -1 means "use the entire distance between the checking entity and the target."
        /// Default is 40, which is the TargetDistance max in the MoveToEnemy actions used in
        /// NPC Core UAI; outside this range NPCs effectively can't see targets.
        /// </summary>
        private float maxSelfDist = 40;

        /// <summary>
        /// The minimum distance, from the target entity, that should be considered as cover.
        /// Without this distance, the cover can be inside the range of the "backup from target"
        /// distance, and the entity will go back and forth betwen backing up from the attack
        /// target and running to the cover position.
        /// Default is 5, which is the TargetDistance max in the BackupShortRanged action
        /// used in NPC Core UAI.
        /// </summary>
        private float minTargetDist = 5;

        /// <summary>
        /// The maximum distance, from the target entity, that should be considered as cover.
        /// Without this distance, the cover can be outside the range of the attack target
        /// distance, and the entity will go back and forth betwen running to the attack target
        /// and running to the cover position.
        /// Default is 34, which is the TargetDistance min in the MoveToEnemyLongRanged action
        /// used in NPC Core UAI.
        /// </summary>
        private float maxTargetDist = 34;

        // These are helper variables to hold the square distances (magnitudes of vectors).
        // Square Dists are easier to calculate because they avoid square roots.
        private float minSelfSqrDist = 0;
        private float maxSelfSqrDist = 40 * 40;
        private float minTargetSqrDist = 5 * 5;
        private float maxTargetSqrDist = 34 * 34;

        // Hit mask for Voxel.Raycast
        private static readonly int HitMask =
            (int)SCoreUtils.HitMasks.CollideArrows |
            (int)SCoreUtils.HitMasks.CollideBullets |
            (int)SCoreUtils.HitMasks.CollideRockets;

        // The amount of time, in seconds, a cover position can spend in the EntityCoverManager
        // before it is purged. This effectively turns the ECM into an LRU cache.
        // This value is also used to throttle the time between calls to purge the ECM.
        private const float COVER_POSITION_CACHE_TIME = 60;
        // The last time expired cover positions were purged from the EntityCoverManager.
        private static float lastCoverPurgeTime = Time.time;

        // Parameter keys
        private static readonly string ParamSelfDistance = "self_distance";
        private static readonly string ParamTargetDistance = "target_distance";

        // For logging
        private static readonly string AdvFeatureClass = "AdvancedTroubleshootingFeatures";
        private static readonly string Feature = "UtilityAILogging";

        /// <summary>
        /// Helper property to get EntityCoverManager.Instance
        /// </summary>
        private EntityCoverManager CoverManager => EntityCoverManager.Instance;

        /// <summary>
        /// Helper property to get PathFinderThread.Instance
        /// </summary>
        private PathFinderThread PathFinder => PathFinderThread.Instance;

        protected override void initializeParameters()
        {
            base.initializeParameters();
            Vector2 minMax;
            if (Parameters.ContainsKey(ParamSelfDistance))
            {
                minMax = ParseIntoVector(Parameters[ParamSelfDistance], minSelfDist);
                minSelfDist = minMax.x;
                minSelfSqrDist = minMax.x * minMax.x;
                maxSelfDist = minMax.y;
                maxSelfSqrDist = minMax.y * minMax.y;
            }
            if (Parameters.ContainsKey(ParamTargetDistance))
            {
                minMax = ParseIntoVector(Parameters[ParamTargetDistance], minTargetDist);
                minTargetDist = minMax.x;
                minTargetSqrDist = minMax.x * minMax.x;
                maxTargetDist = minMax.y;
                maxTargetSqrDist = minMax.y * minMax.y;
            }
        }

        public override void Start(Context _context)
        {
            DisplayLog($"Start: self_distance=({minSelfDist},{maxSelfDist}); target_distance=({minTargetDist},{maxTargetDist})", _context);
            base.Start(_context);

            if (!CanStart(_context.Self, _context.ActionData.Target as EntityAlive))
            {
                DisplayLog("Start: Can't start: stopping", _context);
                Stop(_context);
                return;
            }

            if (ExistingCoverInvalidated(_context))
            {
                DisplayLog("Start: existing cover invalidated: stop moving", _context);
                StopMoving(_context);
                SCoreUtils.SetCrouching(_context);
            }

            PurgeExpiredCoverPositions(_context);
        }

        public override void Update(Context _context)
        {
            var theEntity = _context.Self;

            // If we're already using cover, no work needed
            if (IsUsingCover(theEntity) && !ExistingCoverInvalidated(_context))
            {
                DisplayLog("Update: Is already using valid cover: stop the task", _context);
                Stop(_context);
                return;
            }

            // If we're at the end of the path, finish and return
            if (IsAtCoverPosition(theEntity))
            {
                DisplayLog("Update: Is at cover position: finish", _context);
                Finish(_context);
                return;
            }
            
            if (theEntity.navigator.noPathAndNotPlanningOne())
            {
                // If we have a cover position, we tried pathing to it but can't reach it
                var coverPos = CoverManager.GetCoverPos(theEntity.entityId);
                if (coverPos != null)
                {
                    DisplayLog("Update: Cover was reserved but couldn't path to it: free cover and and stop the task", _context);
                    CoverManager.FreeCover(theEntity.entityId);
                    // Remove it so we won't use it again when searching for existing cover
                    CoverManager.CoverPoints.Remove(coverPos);
                    Stop(_context);
                }

                // We haven't calculated a path, start now
                DisplayLog("Update: Not calculating path: find path to cover", _context);
                FindPathToCover(_context);
                return;
            }

            // If we're still calculating a path, wait until the next update
            if (PathFinder.IsCalculatingPath(theEntity.entityId))
            {
                DisplayLog("Update: Still calculating path: wait until the next update", _context);
                return;
            }

            DisplayLog("Update: Not calculating path: moving to cover, wait until the next update", _context);
        }

        //public override void Stop(Context _context)
        //{
        //    base.Stop(_context);
        //}

        //public override void Reset(Context _context)
        //{
        //    base.Reset(_context);
        //}

        private bool CanStart(EntityAlive theEntity, EntityAlive threatTarget)
        {
            if (theEntity.sleepingOrWakingUp || theEntity.bodyDamage.CurrentStun != 0 || (theEntity.Jumping && !theEntity.isSwimming))
            {
                return false;
            }

            if (threatTarget == null)
            {
                return false;
            }

            return true;
        }

        private void DisplayLog(string method, Context _context)
        {
            AdvLogging.DisplayLog(
                AdvFeatureClass,
                Feature,
                $"{GetType()} {method}: {_context.Self.EntityName} ( {_context.Self.entityId} )");
        }
        
        private bool ExistingCoverInvalidated(Context _context)
        {
            var entityId = _context.Self.entityId;
            var coverPos = CoverManager.GetCoverPos(entityId);
            if (coverPos == null)
            {
                return false;
            }

            if (coverPos.BlockPos.SqrDistance(_context.Self.GetPosition()) > 3)
            {
                DisplayLog("NeedsNewCover: entity is no longer at cover location", _context);
                FreeCover(entityId, coverPos);
                return true;
            }

            if (!IsValidCoverBlock(_context, coverPos.BlockPos, GetTargetPosition(_context.ActionData.Target)))
            {
                DisplayLog($"NeedsNewCover: {coverPos.BlockPos} is no longer valid cover for this target", _context);
                FreeCover(entityId, coverPos);
                return true;
            }

            return false;
        }

        private Bounds FindBounds(Vector3 selfPosition, Vector3 targetPosition)
        {
            var xMinBound = FindMinimumXZ(selfPosition.x, targetPosition.x);
            var xMaxBound = FindMaximumXZ(selfPosition.x, targetPosition.x);
            var zMinBound = FindMinimumXZ(selfPosition.z, targetPosition.z);
            var zMaxBound = FindMaximumXZ(selfPosition.z, targetPosition.z);

            return new Bounds
            {
                // Use -0.5 and 0.5 for y to allow for some leeway in Contains(Vector3)
                min = new Vector3(xMinBound, -0.5f, zMinBound),
                max = new Vector3(xMaxBound, 0.5f, zMaxBound),
            };
        }

        private void FindPathToCover(Context _context)
        {
            // Self position should be centered (it will be used for pathing)
            Vector3 selfPosition =
                //_context.Self.getHipPosition().ToVector3Center();
                _context.Self.getHipPosition().ToVector3CenterXZ();
                //World.worldToBlockPos(_context.Self.getHipPosition());

            var targetPosition = GetTargetPosition(_context.ActionData.Target);

            // This will be a vector pointing in the direction of the target
            var selfToTarget = selfPosition - targetPosition;
            var selfToTargetSqrDist = selfToTarget.sqrMagnitude;

            if (maxSelfDist < 0)
            {
                maxSelfDist = selfToTarget.magnitude;
                maxSelfSqrDist = selfToTargetSqrDist;
            }

            // Make sure there's an overlapping area between the self and target max distances
            if (maxTargetDist != -1)
            {
                // Subtract both the max self and max target distances. If it's > 0 then there is
                // some distance between the max and min, so there is no overlapping area.
                if (selfToTargetSqrDist - maxSelfSqrDist - maxTargetSqrDist > 0)
                {
                    DisplayLog("FindPathToCover: No overlap in area between max distances: stopping", _context);
                    Stop(_context);
                    return;
                }
            }

            // If the distance is less than either the self or target min,
            // then we would need to back up to find cover, which we won't do.
            // TODO or won't we?... See if this is needed/wanted
            if (minSelfDist > 0 && minTargetDist > 0)
            {
                if (selfToTargetSqrDist < minSelfSqrDist || selfToTargetSqrDist < minTargetSqrDist)
                {
                    DisplayLog("FindPathToCover: Too close to target: stopping", _context);
                    Stop(_context);
                    return;
                }
            }

            var bounds = FindBounds(selfPosition, targetPosition);

            var coverPos = SearchAreaForExistingCover(_context, selfPosition, selfToTarget, bounds);

            if (coverPos == Vector3.zero)
            {
                DisplayLog($"FindPathToCover: Could not find a valid existing cover position: searching area around {selfPosition}", _context);
                coverPos = SearchAreaForCover(_context, selfPosition, targetPosition, selfToTarget, bounds);
            }

            if (coverPos == Vector3.zero)
            {
                DisplayLog("FindPathToCover: Could not find a valid cover position: stopping", _context);
                Stop(_context);
                return;
            }

            CoverManager.AddCover(coverPos, selfToTarget.ToCompassDirection());
            CoverManager.MarkReserved(_context.Self.entityId, coverPos);

            DisplayLog($"FindPathToCover: Cover position found, FindPath to {coverPos}", _context);

            PathFinder.FindPath(_context.Self, _context.Self.position, coverPos, _context.Self.moveSpeedAggro, false, null);
        }

        private float FindMaximumXZ(float self, float target)
        {
            var selfMin = self + maxSelfDist;

            if (self > target && maxTargetDist >= 0)
            {
                var targetMax = target + maxTargetDist;
                return Math.Min(selfMin, targetMax);
            }

            return selfMin;
        }

        private float FindMinimumXZ(float self, float target)
        {
            var selfMin = self - maxSelfDist;

            if (self < target && maxTargetDist > 0)
            {
                var targetMax = target - maxTargetDist;
                return Math.Max(selfMin, targetMax);
            }

            return selfMin;
        }

        private void Finish(Context _context)
        {
            DisplayLog("Finish: use cover, stop moving, and stop the task", _context);

            var coverPos = CoverManager.GetCoverPos(_context.Self.entityId);
            CoverManager.UseCover(_context.Self.entityId, coverPos.BlockPos);
            StopMoving(_context);
            Stop(_context);
        }

        private void FreeCover(int entityId, EntityCoverManager.CoverPos coverPos)
        {
            coverPos.Reserved = false;
            coverPos.InUse = false;
            CoverManager.FreeCover(entityId);
        }

        private Vector3 GetTargetPosition(object target)
        {
            if (target is EntityAlive entity)
            {
                // Target position should be raw eye level so cover blocks line of *sight*
                return entity.getHeadPosition();
            }
            else if (target is Vector3 vector)
            {
                return vector;
            }
            return Vector3.zero;
        }

        private bool IsAtCoverPosition(EntityAlive theEntity)
        {
            return CoverManager.GetCoverPos(theEntity.entityId) != null &&
                theEntity.navigator.getPath()?.isFinished() == true;
        }

        private bool IsPositionBlocked(Context _context, Vector3 start, Vector3 end)
        {
            // Voxel.Raycast: used in SCore, but more expensive - TODO also doesn't work well
            //var direction = end - start;
            //var ray = new Ray(start, direction);
            //return Voxel.Raycast(_context.Self.world, ray, direction.magnitude, HitMask, 0.0f);

            // EUtils.isPositionBlocked: used in EAITakeCover, less expensive, but wrong...?
            // Note: layer mask value of 65536 taken from vanilla EAITakeCover
            return EUtils.isPositionBlocked(start, end, 65536);
        }

        private bool IsUsingCover(EntityAlive self)
        {
            return CoverManager.HasCover(self.entityId);
        }

        private bool IsValidCoverBlock(Context _context, Vector3 coverPosition, Vector3 targetPosition)
        {
            // TODO removing these since they seem to be bad checks
            // Block check - checking that the one at waist-level is air and the one beneath it is solid
            //var coverBlockPosition = World.worldToBlockPos(coverPosition);
            //var coverBlock = _context.Self.world.GetBlock(coverBlockPosition);
            //if (!coverBlock.isair)
            //{
            //    DisplayLog($"IsValidCover: Block at {coverPosition} is not air, can't move into it", _context);
            //    return false;
            //}

            //var groundBlockPos = coverBlockPosition;
            //groundBlockPos.y--;
            //var groundBlock = _context.Self.world.GetBlock(groundBlockPos);
            //if (!groundBlock.Block.IsMovementBlocked(_context.Self.world, groundBlockPos, groundBlock, BlockFace.Top))
            //{
            //    DisplayLog($"IsValidCover: Block below {coverPosition} does not block movement, can't stand on it", _context);
            //    return false;
            //}

            if (!IsPositionBlocked(_context, targetPosition, coverPosition))
            {
                DisplayLog($"IsValidCover: Block at {coverPosition} does not block attacks from {targetPosition}", _context);
                return false;
            }
            DisplayLog($"IsValidCover: Block at {coverPosition} DOES block attacks from {targetPosition}", _context);

            // To be valid cover, the block must block attacks, but the one at eye level must not
            // TODO this doesn't work, commenting until I can figure it out
            var eyePos = coverPosition;
            eyePos.y += _context.Self.GetEyeHeight();
            if (IsPositionBlocked(_context, targetPosition, eyePos))
            {
                DisplayLog($"IsValidCover: Block at eye height {eyePos} also blocks attacks from {targetPosition}, not good cover", _context);
                return false;
            }
            DisplayLog($"IsValidCover: Block at eye height {eyePos} does not blocks attacks from {targetPosition}", _context);

            return true;
        }

        private bool IsValidCoverPosition(Context _context,
            Vector3 coverPosition,
            Vector3 selfPosition,
            Vector3 targetPosition,
            Vector3 selfToTarget,
            float minSqrDist = float.MaxValue)
        {
            // Don't search the quadrant in the opposite direction from the target
            if (OppositeQuadrant(coverPosition, selfPosition, selfToTarget))
            {
                DisplayLog($"FindPathToCover: {coverPosition} in quadarant opposite {selfToTarget}", _context);
                return false;
            }

            var selfSqrDist = selfPosition.SqrDistance(coverPosition);
            var targetSqrDist = targetPosition.SqrDistance(coverPosition);

            // Don't bother if we've already found a cover position that is closer to us
            if (selfSqrDist >= minSqrDist)
            {
                DisplayLog($"FindPathToCover: {selfSqrDist} is further away than {minSqrDist}", _context);
                return false;
            }

            // Check range, since we're probably searching a 2D grid and the corners are out of range
            if (selfSqrDist < minSelfSqrDist || selfSqrDist > maxSelfSqrDist)
            {
                DisplayLog($"FindPathToCover: {selfSqrDist} is not in self range ({minSelfSqrDist}, {maxSelfSqrDist})", _context);
                return false;
            }
            if (targetSqrDist < minTargetSqrDist || targetSqrDist > maxTargetSqrDist)
            {
                DisplayLog($"FindPathToCover: {targetSqrDist} is not in target range ({minTargetSqrDist}, {maxTargetSqrDist})", _context);
                return false;
            }

            if (!IsValidCoverBlock(_context, coverPosition, selfToTarget))
            {
                return false;
            }

            if (!CoverManager.IsFree(coverPosition))
            {
                DisplayLog($"FindPathToCover: {coverPosition} is already in use", _context);
                return false;
            }

            return true;
        }

        private bool OppositeQuadrant(Vector3 position, Vector3 selfPosition, Vector3 selfToTarget)
        {
            return (selfToTarget.x > 0 ? selfPosition.x > position.x : selfPosition.x < position.x)
                && (selfToTarget.z > 0 ? selfPosition.z > position.z : selfPosition.z < position.z);
        }

        /// <summary>
        /// Parses a parameter value into a vector where x=min and y=max.
        /// Handles cases where only a max value is specified in the parameter value;
        /// in this case, the min value will be the provided default min.
        /// </summary>
        /// <param name="paramValue"></param>
        /// <param name="defaultMin"></param>
        /// <returns></returns>
        private static Vector2 ParseIntoVector(string paramValue, float defaultMin)
        {
            var minMax = StringParsers.ParseVector2(paramValue);
            if (minMax != Vector2.zero)
            {
                return minMax;
            }

            var max = StringParsers.ParseFloat(paramValue);
            return new Vector2(defaultMin, max);
        }

        private void PurgeExpiredCoverPositions(Context _context)
        {
            var time = Time.time;
            if (lastCoverPurgeTime + COVER_POSITION_CACHE_TIME > time)
                return;

            DisplayLog("Purging expired cover positions..", _context);
            lastCoverPurgeTime = time;
            var purgeCount = 0;
            for (var i = CoverManager.CoverPoints.Count - 1; i >= 0; i--)
            {
                var coverPoint = CoverManager.CoverPoints[i];
                if (coverPoint.InUse || coverPoint.Reserved)
                {
                    // Repurpose "TimeCreated" to "TimeLastUsed" to make this an LRU cache
                    coverPoint.TimeCreated = time;
                    continue;
                }
                if (coverPoint.TimeCreated + COVER_POSITION_CACHE_TIME < time)
                {
                    CoverManager.CoverPoints.RemoveAt(i);
                    purgeCount++;
                }
            }
            DisplayLog($"Purged {purgeCount} expired cover positions", _context);
        }

        // Working but inefficient version - searches the entire grid and returns the closest
        //private Vector3 SearchAreaForCover(Context _context, Vector3 selfPosition, Vector3 targetPosition, Vector3 selfToTarget)
        //{
        //    var xInit = FindMinimumXZ(selfPosition.x, targetPosition.x);
        //    var xMax = FindMaximumXZ(selfPosition.x, targetPosition.x);
        //    var zInit = FindMinimumXZ(selfPosition.z, targetPosition.z);
        //    var zMax = FindMaximumXZ(selfPosition.z, targetPosition.z);

        //    var coverPos = Vector3.zero;
        //    var minSqrDist = float.MaxValue;

        //    var blockVector = new Vector3(0, selfPosition.y, 0);
        //    for (var x = xInit; x < xMax; x += 1)
        //    {
        //        for (var z = zInit; z < zMax; z += 1)
        //        {
        //            blockVector.x = x;
        //            blockVector.z = z;

        //            if (!IsValidCoverPosition(blockVector, selfPosition, selfToTarget, minSqrDist, _context))
        //            {
        //                continue;
        //            }

        //            minSqrDist = SqrDistance(selfPosition, blockVector);
        //            coverPos = ToVector3CenterXZ(blockVector);
        //        }
        //    }

        //    return coverPos;
        //}

        // More efficient version - searches in outward spiral from self, so first position found is closest
        private Vector3 SearchAreaForCover(
            Context _context,
            Vector3 selfPosition,
            Vector3 targetPosition,
            Vector3 selfToTarget,
            Bounds area)
        {
            var coverPosition = selfPosition;
            for (var dist = minSelfDist; dist <= maxSelfDist; dist++)
            {
                // If distance is zero, just check our current block position
                if (Mathf.Approximately(dist, 0))
                {
                    if (IsValidCoverBlock(_context, coverPosition, selfToTarget))
                    {
                        return coverPosition;
                    }
                    continue;
                }

                // Needed so we stay within x/z min and max boundaries
                var minX = Mathf.Max(area.min.x, selfPosition.x - dist);
                var maxX = Mathf.Min(area.max.x, selfPosition.x + dist);
                if (minX > maxX)
                {
                    continue;
                }

                var minZ = Mathf.Max(area.min.z, selfPosition.z - dist);
                var maxZ = Mathf.Min(area.max.z, selfPosition.z + dist);
                if (minZ > maxZ)
                {
                    continue;
                }

                // Edge 1: x = min, z = min -> max
                coverPosition.x = minX;
                for (coverPosition.z = minZ; coverPosition.z <= maxZ; coverPosition.z++)
                {
                    if (IsValidCoverPosition(
                        _context,
                        coverPosition,
                        selfPosition,
                        targetPosition,
                        selfToTarget))
                    {
                        return coverPosition;
                    }
                }

                // Edge 2: x = (min + 1) -> max, z = max
                // (blockPosition.z is already at max from previous edge iteration)
                for (coverPosition.x = minX + 1; coverPosition.x <= maxX; coverPosition.x++)
                {
                    if (IsValidCoverPosition(
                        _context,
                        coverPosition,
                        selfPosition,
                        targetPosition,
                        selfToTarget))
                    {
                        return coverPosition;
                    }
                }

                // Edge 3: x = max, z = (max - 1) -> min
                // (blockPosition.x is already at max from previous edge iteration)
                for (coverPosition.z = maxZ - 1; coverPosition.z >= minZ; coverPosition.z--)
                {
                    if (IsValidCoverPosition(
                        _context,
                        coverPosition,
                        selfPosition,
                        targetPosition,
                        selfToTarget))
                    {
                        return coverPosition;
                    }
                }

                // Edge 4: x = (max - 1) -> (min + 1), z = min
                // (blockPosition.z is already at min from previous edge iteration)
                for (coverPosition.x = maxX - 1; coverPosition.x >= minX + 1; coverPosition.x--)
                {
                    if (IsValidCoverPosition(
                        _context,
                        coverPosition,
                        selfPosition,
                        targetPosition,
                        selfToTarget))
                    {
                        return coverPosition;
                    }
                }
            }

            return Vector3.zero;
        }

        private Vector3 SearchAreaForExistingCover(
            Context context,
            Vector3 selfPosition,
            Vector3 selfToTarget,
            Bounds area)
        {
            var existingCover = Vector3.zero;
            var coverDir = selfToTarget.ToCompassDirection();

            var minDistSq = float.MaxValue;
            foreach (var coverPoint in CoverManager.CoverPoints)
            {
                // These checks should be ordered from least to most computationally expensive
                if (!(coverPoint.Reserved || coverPoint.InUse) &&
                    area.Contains(coverPoint.BlockPos) &&
                    selfPosition.SqrDistance(coverPoint.BlockPos) < minDistSq &&
                    coverDir == coverPoint.CoverDir)
                {
                    existingCover = coverPoint.BlockPos;
                }
            }

            return existingCover;
        }

        private void StopMoving(Context _context)
        {
            PathFinder.RemovePathsFor(_context.Self.entityId);
            _context.Self.navigator.clearPath();
            _context.Self.moveHelper.Stop();

            // Make sure we face the target or else we can't see them and won't exit cover
            if (_context.ActionData.Target is EntityAlive entityAlive)
            {
                _context.Self.RotateTo(entityAlive, 30f, 30f);
                _context.Self.SetLookPosition(entityAlive.getHeadPosition());
            }
        }
    }
}
