using System.Collections.Generic;
using DOL.GS;

namespace DOL.AI
{
    public class FSM
    {
        protected Dictionary<eFSMStateType, FSMState> _states = new();
        protected FSMState _state;
        protected FSMState _previousState;

        public FSM() { }

        public virtual void Add(FSMState state)
        {
            _states.Add(state.StateType, state);
        }

        public virtual void ClearStates()
        {
            _states.Clear();
        }

        public virtual FSMState GetState(eFSMStateType stateType)
        {
            _states.TryGetValue(stateType, out FSMState state);
            return state;
        }

        public virtual void SetCurrentState(eFSMStateType stateType)
        {
            if (_state != null)
            {
                _state.Exit();
                _previousState = _state; // Save the current state as the previous state before changing
            }

            _states.TryGetValue(stateType, out _state);

            if (_state != null)
                _state.Enter();
        }

        public virtual FSMState GetCurrentState()
        {
            return _state;
        }

          public virtual FSMState GetPreviousState()
        {
            return _previousState; // Method to get the previous state
        }

        public virtual void Think()
        {
            if (_state != null)
                _state.Think();
        }

        public virtual void KillFSM() { }
    }
}
