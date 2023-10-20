using ExtUtilsForEnt;
using GamePath;
using System.Collections.Generic;
using UnityEngine;
using static EAITakeCover;

namespace UAI
{
    /// <summary>
    /// This is a UAI adaptation of the vanilla EAITakeCover class.
    /// </summary>
    public class UAITaskTakeCover : UAITaskBase
    {
        // TODO Since Cover is never used, could we just use a new Vector3 with Dist as its magnitude?
        private class PosData
        {
            public Vector3 Dir { get; protected set; }

            public float Dist { get; protected set; }

            // TODO never used
            //public float Cover { get; protected set; }

            public PosData(Vector3 _dir, float _dist)//, float _cover = 0.5f)
            {
                Dir = _dir;
                Dist = _dist;
                //Cover = _cover;
            }
        }

        private static readonly string AdvFeatureClass = "AdvancedTroubleshootingFeatures";
        private static readonly string Feature = "UtilityAILogging";

        private static Vector3 halfBlockOffset = Vector3.one * 0.5f;

        // Renamed to be plural (from "Axis" to "Axes")
        private static Vector3[] mainBlockAxes = new Vector3[8]
        {
            new Vector3(0f, 0f, 1f),
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 0f, -1f),
            new Vector3(-1f, 0f, 0f),
            // TODO these should be roughly 0.7 (precisely, sqrt(2)/2) to lie on the unit circle
            new Vector3(0.5f, 0f, 0.5f),
            new Vector3(0.5f, 0f, -0.5f),
            new Vector3(-0.5f, 0f, -0.5f),
            new Vector3(-0.5f, 0f, 0.5f)
        };

        // TODO rename these when done copying code from EAITakeCover

        // TODO this could just as easily come from _context.Self.world
        private World _world;
        /// <summary>
        /// Lazy initializer to get GameManager.Instance.World
        /// </summary>
        private World world => _world ??= GameManager.Instance.World;

        private PathFinderThread _pathFinder;
        /// <summary>
        /// Lazy initializer to get PathFinderThread.Instance
        /// </summary>
        private PathFinderThread pathFinder => _pathFinder ??= PathFinderThread.Instance;

        private EntityCoverManager _ecm;
        /// <summary>
        /// Lazy initializer to get EntityCoverManager.Instance
        /// </summary>
        private EntityCoverManager ecm => _ecm ??= EntityCoverManager.Instance;

        private void DisplayLog(string method, Context _context)
        {
            AdvLogging.DisplayLog(
                AdvFeatureClass,
                Feature,
                $"{GetType()} {method}: {_context.Self.EntityName} ( {_context.Self.entityId} )");
        }

        //public override void Init(Context _context)
        //{
        //    base.Init(_context);
        //}

        //protected override void initializeParameters()
        //{
        //}

        public override void Start(Context _context)
        {
            DisplayLog("Start", _context);

            if (!CanExecute(_context.Self, _context.ActionData.Target as EntityAlive))
            {
                DisplayLog($"Start: can't execute, stopping", _context);
                Stop(_context);
                return;
            }

            base.Start(_context);

            // TODO seeing what happens when we don't remove the paths
            //PathFinderThread.Instance.RemovePathsFor(_context.Self.entityId);
        }

        public override void Update(Context _context)
        {
            // Private EAITakeCover fields that can't be fields here because UAI works with multiple entities
            // Refactor and remove these when done
            //var findingPath = false;
            var currentPath = new List<Vector3>();
            var state = State.FindPath;
            //var coverTicks = 60;
            //var retryPathTicks = 60f;
            //var random = world.aiDirector.random;
            var pathEnd = Vector3.zero;

            // Helper variables to match EAITakeCover fields
            var theEntity = _context.Self;
            var threatTarget = _context.ActionData.Target as EntityAlive;

            DisplayLog("Update", _context);

            //base.Update(_context); // Not needed if we are extending UAITaskBase

            // TODO probably don't need this?
            //if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer || World == null)
            //{
            //    return;
            //}

            // TODO the HasCover check is the only one relevant in updateCover - this checks if
            // the entity has a cover position, and the position is actually being used
            var hasCover = ecm.HasCover(theEntity.entityId);// updateCover(theEntity);
            if (threatTarget == null || hasCover)
            {
                DisplayLog($"Update: threatTarget={threatTarget}, hasCover={hasCover}, stopping", _context);
                Stop(_context);
                return;
            }

            if (!Continue(theEntity, threatTarget))
            {
                DisplayLog($"Update: can't continue, stopping", _context);
                Stop(_context);
                return;
            }

            var pathInfo = pathFinder.GetPath(theEntity.entityId);
            switch (state)
            {
                //case State.Idle:
                //    if (!findingPath)
                //    {
                //        findingPath = true;
                //        state = State.FindPath;
                //    }
                //    break;
                case State.FindPath:
                    if (!pathFinder.IsCalculatingPath(theEntity.entityId)
                        // TODO making sure that we're not repeatedly re-calculating the path
                        //&& theEntity.navigator.noPathAndNotPlanningOne()
                        //&& pathFinder.GetPath(theEntity.entityId)?.path == null)
                        && pathInfo?.path == null)
                    {
                        DisplayLog("Update: FindPath: Not calculating path and pathInfo.path is null: finding initial path", _context);
                        FindPath(theEntity, threatTarget);
                        //Vector3 vector = findCoverDir(theEntity, threatTarget);
                        //pathFinder.RemovePathsFor(theEntity.entityId);
                        //pathFinder.FindPath(theEntity, threatTarget.getHipPosition() + vector * 10f * 2f, theEntity.moveSpeedAggro, _canBreak: false, null);// this);
                        //state = State.PreProcessPath;
                        return;
                    }
                    //break;
                    goto case State.PreProcessPath;
                case State.PreProcessPath:
                    //var pathInfo = pathFinder.GetPath(theEntity.entityId);
                    if (pathFinder.IsCalculatingPath(theEntity.entityId) || pathInfo?.path == null)
                    {
                        DisplayLog("Update: PreProcessPath: IsCalculatingPath or pathInfo?.path is null: wait until next update", _context);
                        //break;
                        return;
                    }

                    DisplayLog($"Update: PreProcessPath: Calculated path has {pathInfo.path.points.Length} points", _context);
                    currentPath.Clear();
                    var coverFound = false;
                    var pathPointsExamined = 0;
                    var coverPositions = new List<Vector3>();
                    for (int i = 0; i < pathInfo.path.points.Length; i++)
                    {
                        var projectedLocation = pathInfo.path.points[i].projectedLocation;
                        currentPath.Add(projectedLocation);
                        var projectedLocationHip = matchHipHeight(theEntity, projectedLocation);

                        // TODO getBestCoverDirection could just return a single CoverCastInfo object,
                        // the same way the overload does for a vector, returning zero if the list is empty
                        var bestCoverDirection = getBestCoverDirection(projectedLocationHip, threatTarget.getHipPosition(), 10f);
                        var dir = Vector3.zero;
                        var hitPoint = Vector3.zero;
                        if (bestCoverDirection.Count > 0)
                        {
                            dir = bestCoverDirection[0].Dir;
                            hitPoint = bestCoverDirection[0].HitPoint;
                        }

                        var hitPointCentered = new Vector3i(hitPoint).ToVector3CenterXZ();
                        // TODO may need to remove one or both of these checks, testing
                        if (
                            !EUtils.isPositionBlocked(projectedLocationHip, threatTarget.getChestPosition(), 65536)
                            //||
                            //!(hitPointCentered != pathEnd)
                            )
                        {
                            DisplayLog($"Update: PreProcessPath: {projectedLocationHip} does not block attacks", _context);//, OR {hitPointCentered}={pathEnd}", _context);
                            continue;
                        }

                        coverPositions.Add(hitPointCentered);
                        if (pathPointsExamined > 3 || i >= pathInfo.path.points.Length - 1)
                        {
                            int index = 0;
                            float minDistance = float.MaxValue;
                            for (int j = 0; j < coverPositions.Count; j++)
                            {
                                EUtils.DrawBounds(new Vector3i(coverPositions[j]), Color.red * Color.yellow * 0.5f, 10f);
                                float distance = Vector3.Distance(coverPositions[j], theEntity.position);
                                if (distance < minDistance && EUtils.isPositionBlocked(coverPositions[j], threatTarget.getChestPosition(), 65536) && ecm.IsFree(coverPositions[j]))
                                {
                                    index = j;
                                    minDistance = distance;
                                }
                            }

                            var coverPos = coverPositions[index];
                            pathEnd = new Vector3i(coverPos).ToVector3CenterXZ();
                            ecm.AddCover(pathEnd, dir);
                            ecm.MarkReserved(theEntity.entityId, pathEnd);
                            EUtils.DrawLine(projectedLocationHip, coverPos, Color.red, 10f);
                            EUtils.DrawBounds(new Vector3i(coverPos), Color.green, 10f);

                            DisplayLog($"Update: PreProcessPath: Cover position found, pathFinder.FindPath to {pathEnd}", _context);

                            pathFinder.FindPath(theEntity, theEntity.position, pathEnd, theEntity.moveSpeedAggro, _canBreak: false, null);// this);
                            coverFound = true;
                            break;
                        }

                        pathPointsExamined++;
                    }

                    //DisplayLog($"Update: PreProcessPath: Cover {(coverFound ? "" : "NOT ")}found, currentPath.Count={currentPath.Count}", _context);
                    // Inverted if-statement to avoid fallthrough/goto
                    if (!coverFound || currentPath.Count <= 0)
                    {
                        DisplayLog($"Update: PreProcessPath: Cover not found and path count zero: Freeing cover and stopping", _context);
                        //freeCover();
                        ecm.FreeCover(theEntity.entityId);
                        //retryPathTicks = 60f;
                        //state = State.FindPath;
                        Stop(_context);
                        return;
                    }
                    EUtils.DrawPath(currentPath, Color.white, Color.yellow);
                    //state = State.ProcessPath;
                    goto case State.ProcessPath;
                case State.ProcessPath:
                    // Removing because ticks are unique to an entity and UAI tasks are not
                    //if (retryPathTicks > 0f)
                    //{
                    //    retryPathTicks -= 1f;
                    //    if (retryPathTicks <= 0f)
                    //    {
                    //        //freeCover();
                    //        ecm.FreeCover(theEntity.entityId);
                    //        retryPathTicks = 60f;
                    //        state = State.FindPath;
                    //        break;
                    //    }
                    //}

                    if (currentPath.Count > 0)
                    {
                        DisplayLog($"Update: ProcessPath: currentPath.Count={currentPath.Count}", _context);
                        // Refactoring to use SqrMagnitude rather than Distance to avoid square root
                        if (Vector3.SqrMagnitude(theEntity.position - pathEnd) < 1)//0.25f)
                           //Vector3.Distance(theEntity.position, pathEnd) < 0.5f)
                        {
                            DisplayLog("Update: ProcessPath: Entity in position: Use cover and Stop", _context);
                            // Leaving these here for now, not moving to Stop
                            pathFinder.RemovePathsFor(theEntity.entityId);
                            theEntity.SetLookPosition(threatTarget.getHeadPosition());
                            ecm.UseCover(theEntity.entityId, pathEnd);
                            theEntity.navigator.clearPath();
                            theEntity.moveHelper.Stop();
                            //coverTicks = 20 * random.RandomRange(4);
                            //findingPath = false;
                            //state = State.Idle;

                            // We're here; this task is done
                            Stop(_context);
                        }
                        DisplayLog($"Update: ProcessPath: Entity not in position: Wait until next tick", _context);
                    }
                    else
                    {
                        DisplayLog($"Update: ProcessPath: currentPath.Count=0: FreeCover and Stop", _context);
                        //freeCover();
                        ecm.FreeCover(theEntity.entityId);
                        //retryPathTicks = 60f;
                        //state = State.FindPath;
                        Stop(_context);
                    }
                    break;
            } // end switch
        }

        // Extracted from Update()
        private void FindPath(EntityAlive theEntity, EntityAlive threatTarget)
        {
            var coverDir = findCoverDir(theEntity, threatTarget);
            pathFinder.RemovePathsFor(theEntity.entityId);
            pathFinder.FindPath(theEntity, threatTarget.getHipPosition() + (coverDir * 10f * 2f), theEntity.moveSpeedAggro, _canBreak: false, null);
        }

        public override void Stop(Context _context)
        {
            DisplayLog("Stop", _context);

            base.Stop(_context);
        }

        //public override void Reset(Context _context)
        //{
        //    AdvLogging.DisplayLog(
        //        AdvFeatureClass,
        //        Feature,
        //        $"{GetType()} Start: {_context.Self.EntityName} ( {_context.Self.entityId} )");

        //    base.Reset(_context);
        //}

        // from EAITakeCover
        private bool CanExecute(EntityAlive theEntity, EntityAlive threatTarget)
        {
            if (theEntity.sleepingOrWakingUp || theEntity.bodyDamage.CurrentStun != 0 || (theEntity.Jumping && !theEntity.isSwimming))
            {
                return false;
            }

            if (threatTarget == null)
            {
                return false;
            }

            //if (stopSeekingCover)
            //{
            //    return false;
            //}

            // Removed health check - this should be a UAI consideration
            if (//theEntity.Health < theEntity.GetMaxHealth() &&
                // Changed from distance to square magnitude to avoid calculating square roots
                Vector3.SqrMagnitude(theEntity.position - threatTarget.position) > 25f)
                //Vector3.Distance(theEntity.position, threatTarget.position) > 5f)
            {
                return true;
            }

            return false;
        }

        // from EAITakeCover
        private bool Continue(EntityAlive theEntity, EntityAlive threatTarget)
        {
            // Removed health check - this should be a UAI consideration
            if (//theEntity.Health < theEntity.GetMaxHealth() &&
                // Changed from distance to square magnitude to avoid calculating square roots
                Vector3.SqrMagnitude(theEntity.position - threatTarget.position) < 9f)
                //Vector3.Distance(theEntity.position, threatTarget.position) < 3f)
            {
                return false;
            }

            //if (stopSeekingCover)
            //{
            //    return false;
            //}

            return true;
        }

        // from EAITakeCover 
        private Vector3 findCoverDir(EntityAlive theEntity, EntityAlive threatTarget, bool debugDraw = false)
        {
            var vector = getBestCoverDirection(threatTarget.getHipPosition(), 10f, debugDraw);
            if (vector == Vector3.zero)
            {
                vector = (theEntity.position - threatTarget.position).normalized;
            }

            return vector;
        }

        // from EAITakeCover
        // Refactored to find min distiance position while iterating through mainBlockAxes
        // Note: The "best" direction is the one towards a cover position that is closest TO THE THREAT
        private Vector3 getBestCoverDirection(Vector3 threatPos, float dist, bool debugDraw = false)
        {
            //var blockedPositions = new List<PosData>();

            var dir = Vector3.zero;
            var minDist = float.MaxValue;
            for (int i = 0; i < mainBlockAxes.Length; i++)
            {
                var axisDist = mainBlockAxes[i] * dist;
                if (EUtils.isPositionBlocked(threatPos, threatPos + axisDist, out var hit, 65536, debugDraw))
                {
                    // Removing since only Dist and Dir are used, Cover isn't
                    //var halfBlockUp = new Vector3(0f, 0.5f, 0f);
                    //var cover = 0.5f;
                    //if (EUtils.isPositionBlocked(threatPos + halfBlockUp, threatPos + halfBlockUp + mainBlockAxis[i] * dist, out _, 65536, debugDraw))
                    //{
                    //    cover = 1f;
                    //}

                    //blockedPositions.Add(new PosData(mainBlockAxes[i], hit.distance));//, cover));

                    if (hit.distance < minDist)
                    {
                        minDist = hit.distance;
                        dir = mainBlockAxes[i];
                    }
                }
            }

            //var minDist = float.MaxValue;
            //var index = 0;
            //for (int j = 0; j < blockedPositions.Count; j++)
            //{
            //    if (blockedPositions[j].Dist < minDist)
            //    {
            //        minDist = blockedPositions[j].Dist;
            //        index = j;
            //    }
            //}

            if (dir != Vector3.zero)//blockedPositions.Count > 0)
            {
                if (debugDraw)
                {
                    // TODO bug? Only draws the first position in the list
                    //EUtils.DrawLine(threatPos, threatPos + blockedPositions[0].Dir * dist, Color.green, 10f);
                    EUtils.DrawLine(threatPos, threatPos + dir * dist, Color.green, 10f);
                }

                //return blockedPositions[index].Dir;
            }

            return dir;// Vector3.zero;
        }

        // from EAITakeCover
        // TODO This overload could just return a single CoverCastInfo object,
        // the same way the overload does for a vector, returning zero if the list is empty
        // Note: The "best" direction is the one towards a cover position that is closest TO THE TARGET (the threat's hip position)
        private List<CoverCastInfo> getBestCoverDirection(Vector3 point, Vector3 target, float dist, bool debugDraw = false)
        {
            var list = new List<CoverCastInfo>();
            var _pos = new Vector3i(point).ToVector3Center();
            _pos.y += 0.15f;
            for (int i = 0; i < mainBlockAxes.Length; i++)
            {
                var axisOffset = new Vector3i(mainBlockAxes[i] * dist) - halfBlockOffset;
                if (EUtils.isPositionBlocked(_pos, _pos + axisOffset, out var hit, 65536, debugDraw))
                {
                    list.Add(new CoverCastInfo(
                        _pos,
                        mainBlockAxes[i],
                        hit.point + Origin.position + hit.normal * 0.1f,
                        Vector3.Distance(hit.point + Origin.position, target)));
                }
            }

            list.Sort((CoverCastInfo x, CoverCastInfo y) => x.ThreatDistance.CompareTo(y.ThreatDistance));
            return list;
        }

        // from EAITakeCover 
        private Vector3 matchHipHeight(EntityAlive theEntity, Vector3 point)
        {
            //Vector3 result = theEntity.getHipPosition();
            //float y = result.y;
            var result = point;
            result.y = theEntity.getHipPosition().y;
            return result;
        }

        // from EAITakeCover - and it's badly named, it should be "HasCoverPos" or something
        private bool updateCover(EntityAlive theEntity)
        {
            // TODO if HasCover is true, GetCoverPos will never return null;
            // so either we check if it pos is null, or check if it's in use
            // (which is what HasCover does)

            if (!ecm.HasCover(theEntity.entityId))
            {
                return false;
            }

            if (ecm.GetCoverPos(theEntity.entityId) == null)
            {
                return false;
            }

            // Removing because ticks are unique to an entity and UAI tasks are not
            // In any case this is supposed to randomly make the entity flee from cover,
            // and that's not needed in UAI anyway
            //if (coverTicks > 0)
            //{
            //    coverTicks--;
            //    if (coverTicks <= 0)
            //    {
            //        if (base.Random.RandomRange(2) < 1)
            //        {
            //            freeCover();
            //            if (base.Random.RandomRange(2) < 1)
            //            {
            //                stopSeekingCover = true;
            //            }
            //        }
            //        else
            //        {
            //            coverTicks = 60;
            //        }
            //    }
            //}

            return true;
        }
    }
}
 