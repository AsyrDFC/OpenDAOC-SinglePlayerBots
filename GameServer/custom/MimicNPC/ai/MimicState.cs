using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Reflection;
using DOL.GS;
using DOL.GS.Scripts;
using DOL.GS.ServerProperties;
using log4net;
using log4net.Layout;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace DOL.AI.Brain
{
    public class MimicState : FSMState
    {
        protected static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected MimicBrain _brain = null;

        public bool Init;

        public MimicState(MimicBrain brain) : base()
        {
            _brain = brain;
        }

        public override void Think() { }
        public override void Enter() { }
        public override void Exit() { }
    }

    public class MimicState_WakingUp : MimicState
    {
        public MimicState_WakingUp(MimicBrain brain) : base(brain)
        {

            StateType = eFSMStateType.WAKING_UP;
        }

        public override void Think()
        {
            if (!Init)
            {

                _brain.AggroLevel = 100;
                int[] pvpRegionIds = new int[] { 252, 165 }; // Add the region IDs you want to check
                _brain.PvPMode = pvpRegionIds.Contains(_brain.Body.CurrentRegionID);
                _brain.AggroRange = _brain.PvPMode ? 3600 : 1500;
                _brain.Roam = true;
                _brain.Defend = false;
                _brain.Body.RoamingRange = 10000;
                log.Info($"{_brain.Body} is WAKING UP");

                //_brain.CheckDefensiveAbilities();
                //_brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive);

                Init = true;
            }

            if (_brain.Body.Group != null)
            {
                if (_brain.Body.Group.MimicGroup.CampPoint != null && _brain.Body.IsWithinRadius(_brain.Body.Group?.MimicGroup.CampPoint, 1500))
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.CAMP);
                    return;
                }
                else if (_brain.Body.Group.LivingLeader != _brain.Body)
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
                    return;
                }
            }
            if (_brain.Body.CurrentRegion.ID == 252)
            {
                if (!_brain.Body.attackComponent.AttackState && _brain.Body.CanRoam)
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.ROAMING);
                    return;
                }
            }
            else if (_brain.Body.CurrentRegion.ID == 165)
            {
                if (!_brain.Body.attackComponent.AttackState && _brain.HasPatrolPath())
                {
                    _brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive);
                    _brain.FSM.SetCurrentState(eFSMStateType.PATROLLING);
                    return;
                }
            }
            if (!_brain.PreventCombat)
            {
                if (_brain.CheckProximityAggro(_brain.AggroRange))
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }
            }

            _brain.FSM.SetCurrentState(eFSMStateType.IDLE);
            base.Think();
        }
    }

    public class MimicState_Idle : MimicState
    {
        public MimicState_Idle(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.IDLE;
        }

        public override void Enter()
        {
            base.Enter();
        }

        public override void Think()
        {
            _brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive);
            // if (_brain.HasPatrolPath())
            // {
            //     _brain.FSM.SetCurrentState(eFSMStateType.PATROLLING);
            //     return;
            // }

            if (_brain.Body.CurrentRegion.ID == 252)
            {
                if (_brain.Body.CanRoam)
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.ROAMING);
                    return;
                }
            }

            // if (_brain.IsBeyondTetherRange())
            // {
            //    _brain.FSM.SetCurrentState(eFSMStateType.RETURN_TO_SPAWN);
            //    return;
            // }

            // if (!_brain.PreventCombat)
            // {
            //    if (_brain.CheckProximityAggro(_brain.AggroRange))
            //    {
            //        _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
            //        return;
            //    }
            // }

            base.Think();
        }
    }

    public class MimicState_FollowLeader : MimicState
    {
        GameLiving leader;

        public MimicState_FollowLeader(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.FOLLOW_THE_LEADER;
        }

        public override void Enter()
        {

            if (_brain.Body.Group != null)
            {
                leader = _brain.Body.Group.LivingLeader;
                _brain.Body.Follow(_brain.Body.Group.LivingLeader, 200, 5000);
            }
            // else
            // _brain.FSM.SetCurrentState(eFSMStateType.IDLE);

            base.Enter();
        }

        public override void Think()
        {

            if (_brain.Body.Group == null)
            {
                _brain.Body.StopFollowing();
                _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);

                return;
            }

            if (leader == null)
                leader = _brain.Body.Group.LivingLeader;

            //if (!_brain.PreventCombat)
            //{
            //if (_brain.CheckProximityAggro(_brain.AggroRange))
            //{
            //    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
            //    return;
            //}
            //}

            if ((leader.IsCasting || leader.IsAttacking) && leader.TargetObject is GameLiving livingTarget && _brain.CanAggroTarget(livingTarget))
            {
                _brain.AddToAggroList(livingTarget, 1);
                _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                return;
            }

            if (_brain.Body.FollowTarget != leader)
                _brain.Body.Follow(_brain.Body.Group.LivingLeader, 200, 5000);

            if (!_brain.Body.InCombat)
            {
                if (!_brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive))
                    _brain.MimicBody.Sit(_brain.CheckStats(75));
            }

            base.Think();

        }

        public override void Exit()
        {
            _brain.Body.StopFollowing();

            base.Exit();
        }
    }

    public class MimicState_Aggro : MimicState
    {
        private const int LEAVE_WHEN_OUT_OF_COMBAT_FOR = 25000;

        private long _aggroTime = GameLoop.GameLoopTime; // Used to prevent leaving on the first think tick, due to `InCombatInLast` returning false.

        public MimicState_Aggro(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.AGGRO;
        }

        public override void Enter()
        {

            if (_brain.Body.IsSitting)
                _brain.MimicBody.Sit(false);

            _aggroTime = GameLoop.GameLoopTime;
            base.Enter();
        }

        public override void Exit()
        {
            if (_brain.Body.attackComponent.AttackState)
                _brain.Body.StopAttack();

            _brain.Body.TargetObject = null;

            //_brain.Body.SpawnPoint = new Point3D(_brain.Body.X, _brain.Body.Y, _brain.Body.Z);
            base.Exit();
        }

        public override void Think()
        {
            if (_brain.PvPMode && !_brain.HasAggro)
                _brain.CheckProximityAggro(_brain.AggroRange);

            //if (_brain.IsFleeing)
            //{
            //    if (!_brain.Body.IsWithinRadius(_brain.Body.TargetObject, 500))
            //    {
            //        _brain.IsFleeing = false;
            //    }
            //}

            if (!_brain.HasAggro || (!_brain.Body.InCombatInLast(LEAVE_WHEN_OUT_OF_COMBAT_FOR) && _aggroTime + LEAVE_WHEN_OUT_OF_COMBAT_FOR <= GameLoop.GameLoopTime))
            {
                if (!_brain.Body.IsMezzed && !_brain.Body.IsStunned)
                {
                    if (_brain.PvPMode)
                    {
                        if (_brain.Roam)
                        {
                            if (_brain.Body.Group != null)
                            {
                                if (_brain.Body.Group.LivingLeader == _brain.Body)
                                    if (_brain.Body.CurrentRegion.ID == 252)
                                        _brain.FSM.SetCurrentState(eFSMStateType.ROAMING);
                                    else if (_brain.Body.CurrentRegion.ID == 165)
                                        _brain.FSM.SetCurrentState(eFSMStateType.PATROLLING);
                                    else
                                        _brain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
                            }
                            else if (_brain.Body.CurrentRegion.ID == 252)
                                _brain.FSM.SetCurrentState(eFSMStateType.ROAMING);
                            else if (_brain.Body.CurrentRegion.ID == 165)
                                _brain.FSM.SetCurrentState(eFSMStateType.PATROLLING);
                        }
                        else if (_brain.Defend)
                        {
                            if (_brain.Body.Group != null)
                            {
                                if (_brain.Body.Group.LivingLeader == _brain.Body)
                                    _brain.FSM.SetCurrentState(eFSMStateType.RETURN_TO_SPAWN);
                                else
                                    _brain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
                            }
                            else
                                _brain.FSM.SetCurrentState(eFSMStateType.RETURN_TO_SPAWN);
                        }
                    }
                    else
                    {
                        //pve camp
                        if (_brain.Body.Group != null)
                        {
                            if (_brain.Body.Group.MimicGroup.CampPoint != null)
                                _brain.FSM.SetCurrentState(eFSMStateType.CAMP);
                            else if (_brain.Body.Group.LivingLeader == _brain.Body)
                                _brain.FSM.SetCurrentState(eFSMStateType.IDLE);
                            else
                                _brain.FSM.SetCurrentState(eFSMStateType.FOLLOW_THE_LEADER);
                        }
                    }

                    return;
                }
            }

            _brain.AttackMostWanted();

            base.Think();
        }
    }

    public class MimicState_Roaming : MimicState
    {
        private const int ROAM_COOLDOWN = 25 * 1000;
        private long _lastRoamTick = 0;
        private const int ROAM_CHANCE_DEFEND = 20;
        private const int ROAM_CHANCE_ROAM = 99;
        private bool delayRoam;

        public MimicState_Roaming(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.ROAMING;
        }

        public override void Enter()
        {
            if (ECS.Debug.Diagnostics.StateMachineDebugEnabled)
                Console.WriteLine($"{_brain.Body} is entering ROAM");

            base.Enter();
        }

        public override void Exit()
        {
            base.Exit();
        }

        public override void Think()
        {
            if (_brain.Defend && _brain.IsBeyondTetherRange())
            {
                _brain.FSM.SetCurrentState(eFSMStateType.RETURN_TO_SPAWN);
                return;
            }

            if (!_brain.PreventCombat)
            {
                if (_brain.CheckProximityAggro(_brain.AggroRange))
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }
            }

            if (!_brain.Body.InCombat)
            {
                delayRoam = false;
                if (_brain.Body.Group != null)
                {
                    foreach (GameLiving groupMember in _brain.Body.Group.GetMembersInTheGroup())
                        if (groupMember is MimicNPC mimic)
                            if (groupMember.IsCasting || groupMember.IsSitting ||
                                (mimic.MimicBrain.FSM.GetCurrentState() == mimic.MimicBrain.FSM.GetState(eFSMStateType.FOLLOW_THE_LEADER) &&
                                !_brain.Body.IsWithinRadius(groupMember, 1000)))
                                delayRoam = true;
                }
                else
                //check for self buffs
                if (!_brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive))
                    _brain.MimicBody.Sit(_brain.CheckStats(75));

                if (_brain.Body.IsTargetPositionValid && delayRoam)
                {
                    _brain.Body.StopMoving();
                }

                // Ensure that roaming only occurs if not delayed
                if (!delayRoam && !_brain.Body.IsCasting && !_brain.Body.IsSitting)
                {
                    int chance = Properties.GAMENPC_RANDOMWALK_CHANCE;

                    if (_brain.PvPMode)
                        chance = ROAM_CHANCE_ROAM;

                    if (_lastRoamTick + ROAM_COOLDOWN <= GameLoop.GameLoopTime && Util.Chance(chance))
                    {
                        if (!_brain.Body.IsMoving)
                        {
                            _brain.Body.Roam(_brain.Body.MaxSpeed);
                            _lastRoamTick = GameLoop.GameLoopTime;
                        }

                    }
                }
            }

            base.Think();
        }
    }

    public class MimicState_Camp : MimicState
    {
        public int AggroRange = 0;

        public MimicState_Camp(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.CAMP;
        }

        public override void Enter()
        {
            if (ECS.Debug.Diagnostics.StateMachineDebugEnabled)
                Console.WriteLine($"{_brain.Body} is entering CAMP");

            if (_brain.Body.Group?.MimicGroup.CampPoint == null || !_brain.Body.IsWithinRadius(_brain.Body.Group?.MimicGroup.CampPoint, 1500))
            {
                _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                return;
            }

            int randomX = _brain.Body.CurrentRegion.IsDungeon ? Util.Random(-50, 50) : Util.Random(-100, 100);
            int randomY = _brain.Body.CurrentRegion.IsDungeon ? Util.Random(-50, 50) : Util.Random(-100, 100);

            _brain.Body.SpawnPoint = new Point3D(_brain.Body.Group.MimicGroup.CampPoint);
            _brain.Body.SpawnPoint.X += randomX;
            _brain.Body.SpawnPoint.Y += randomY;

            _brain.AggroRange = _brain.Body.CurrentRegion.IsDungeon ? 250 : 550;

            if (AggroRange != 0)
                _brain.AggroRange = AggroRange;

            _brain.ClearAggroList();
            _brain.Body.ReturnToSpawnPoint(_brain.Body.MaxSpeed);
            _brain.IsPulling = false;

            base.Enter();
        }

        public override void Think()
        {
            if (!_brain.IsPulling && _brain.Body.IsTargetPositionValid)
                return;

            if (_brain.IsMainPuller)
                _brain.CheckPuller();

            if (_brain.IsMainCC)
                _brain.CheckMainCC();

            if (!_brain.IsPulling)
            {
                if (_brain.CheckProximityAggro(_brain.AggroRange))
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }
            }

            if (!_brain.Body.IsMoving && !_brain.Body.InCombat)
            {
                if (!_brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive))
                    _brain.MimicBody.Sit(_brain.CheckStats(75));
            }

            base.Think();
        }
    }

    public class MimicState_ReturnToSpawn : MimicState
    {
        public MimicState_ReturnToSpawn(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.RETURN_TO_SPAWN;
        }

        public override void Enter()
        {
            if (ECS.Debug.Diagnostics.StateMachineDebugEnabled)
                Console.WriteLine($"{_brain.Body} is entering RETURN_TO_SPAWN");

            if (_brain.Body.WasStealthed)
                _brain.Body.Flags |= GameNPC.eFlags.STEALTH;

            _brain.ClearAggroList();
            _brain.Body.ReturnToSpawnPoint(GamePlayer.PLAYER_BASE_SPEED);
            base.Enter();
        }

        public override void Think()
        {
            if (!_brain.Body.IsNearSpawn &&
                (!_brain.HasAggro || !_brain.Body.IsEngaging) &&
                (!_brain.Body.IsReturningToSpawnPoint) &&
                _brain.Body.CurrentSpeed == 0)
            {
                _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                _brain.Body.TurnTo(_brain.Body.SpawnHeading);
                return;
            }

            if (_brain.Body.IsNearSpawn)
            {
                _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
                _brain.Body.TurnTo(_brain.Body.SpawnHeading);
                return;
            }

            if (!_brain.PreventCombat)
            {
                if (_brain.CheckProximityAggro(_brain.AggroRange))
                {
                    if (_brain.Body.Group != null && _brain.Body.Group.MimicGroup.CampPoint != null)
                        _brain.FSM.SetCurrentState(eFSMStateType.CAMP);

                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }
            }

            base.Think();
        }
    }

    public class MimicState_Patrolling : MimicState
    {
        private const int PATROL_COOLDOWN = 25 * 1000;
        private long _lastRoamTick = 0;
        private const int PATROL_CHANCE_DEFEND = 20;
        private const int PATROL_CHANCE_PATROL = 99;
        private bool delayRoam;

        public MimicState_Patrolling(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.PATROLLING;
        }

        public override void Enter()
        {
            if (ECS.Debug.Diagnostics.StateMachineDebugEnabled)
                Console.WriteLine($"{_brain.Body} is PATROLLING");
            _brain.ClearAggroList();
            base.Enter();
        }

        public override void Think()
        {
            if (_brain.IsBeyondTetherRange())
            {
                log.Info($"{_brain.Body} is beyond tether range, returning to spawn.");
                _brain.FSM.SetCurrentState(eFSMStateType.RETURN_TO_SPAWN);
            }

            if (!_brain.PreventCombat)
            {
                if (_brain.CheckProximityAggro(_brain.AggroRange))
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }
            }

            if (!_brain.Body.InCombat)
            {
                delayRoam = false;
                if (_brain.Body.Group != null)
                {
                    foreach (GameLiving groupMember in _brain.Body.Group.GetMembersInTheGroup())
                        if (groupMember is MimicNPC mimic)
                            if (groupMember.IsCasting || groupMember.IsSitting ||
                                (mimic.MimicBrain.FSM.GetCurrentState() == mimic.MimicBrain.FSM.GetState(eFSMStateType.FOLLOW_THE_LEADER) &&
                                !_brain.Body.IsWithinRadius(groupMember, 1000)))
                                delayRoam = true;
                }
                else
                //check for self buffs
                if (!_brain.CheckSpells(MimicBrain.eCheckSpellType.Defensive))
                    _brain.MimicBody.Sit(_brain.CheckStats(75));

                if (_brain.Body.IsTargetPositionValid && delayRoam)
                {
                    _brain.Body.StopMoving();
                }

                if (_brain.Body.CurrentRegion.ID == 165)
                {
                    if (!delayRoam && !_brain.Body.IsCasting && !_brain.Body.IsSitting)
                    {
                        int chance = Properties.GAMENPC_RANDOMWALK_CHANCE;

                        if (_brain.PvPMode)
                            chance = PATROL_CHANCE_PATROL;

                        if (_lastRoamTick + PATROL_COOLDOWN <= GameLoop.GameLoopTime && Util.Chance(chance))
                        {
                            if (!_brain.Body.IsMoving && !_brain.Body.IsMovingOnPath)
                            {
                                //GROUP CHECK FOR GROUP SPEED OR SELF SPEED
                                // Check if the entity is part of a group
                                var groupMembers = _brain.Body.Group?.GetMembersInTheGroup();
                                if (groupMembers != null && groupMembers.Any())
                                {
                                    // If part of a group, find the maximum speed among group members
                                    var maxGroupSpeed = groupMembers.Max(member => member.MaxSpeed);
                                    _brain.Body.MoveOnPath(maxGroupSpeed);
                                }
                                else
                                {
                                    // If not part of a group, use the entity's own MaxSpeed
                                    _brain.Body.MoveOnPath(_brain.Body.MaxSpeed);
                                }
                                _lastRoamTick = GameLoop.GameLoopTime;
                            }

                        }
                    }
                }

            }

            base.Think();
        }
    }

    public class MimicState_Dead : MimicState
    {
        public MimicState_Dead(MimicBrain brain) : base(brain)
        {
            StateType = eFSMStateType.DEAD;
        }

        public override void Enter()
        {
            if (ECS.Debug.Diagnostics.StateMachineDebugEnabled)
                Console.WriteLine($"{_brain.Body} has entered DEAD state");

            _brain.ClearAggroList();
            base.Enter();
        }

        public override void Think()
        {
            _brain.FSM.SetCurrentState(eFSMStateType.WAKING_UP);
            base.Think();
        }
    }
}
