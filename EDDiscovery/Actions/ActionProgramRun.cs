﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EDDiscovery.Actions
{
    // this is the run time context of a program, holding run time data

    public class ActionProgramRun : ActionProgram
    {
        // used during execution.. filled in on program objects associated with an execution
        public ActionFile actionfile;                       // what file it came from..
        public ActionRun actionrun;                         // who is running it..
        public EDDiscoveryForm discoveryform;
        public HistoryList historylist;
        public HistoryEntry historyentry;                   // may be null, if the execute uses this, check.
        public ConditionVariables startvars;        // the vars passed in at start (and used to pass to any callers)
        public ConditionVariables currentvars;      // these may be freely modified, they are local to this APR
        public ActionFunctions functions;                   // function handler
        public bool allowpause;

        private int nextstepnumber;     // the next step to execute, 0 based

        public enum ExecState { On, Off, OffForGood }
        private ExecState[] execstate = new ExecState[50];
        Action.ActionType[] exectype = new Action.ActionType[50];   // type of level
        private int[] execlooppos = new int[50];            // if not -1, on level down, go back to this step.
        private int execlevel = 0;

        private string errlist = null;

        public ActionProgramRun(ActionFile af, ActionProgram r, ActionRun runner,
                                EDDiscoveryForm ed, HistoryList hl, HistoryEntry h,
                                ConditionVariables gvars, bool allowp = false) : base(r.Name)      // make a copy of the program..
        {
            actionfile = af;
            actionrun = runner;
            discoveryform = ed;
            historyentry = h;
            historylist = hl;
            functions = new ActionFunctions(ed, hl, h);
            allowpause = allowp;
            execlevel = 0;
            execstate[execlevel] = ExecState.On;
            nextstepnumber = 0;

            System.Diagnostics.Debug.WriteLine("Run " + actionfile.name + "::" + r.Name);
            //ActionData.DumpVars(gvars, " Func Var:");

            startvars = new ConditionVariables(gvars); // keep this, used by call to pass clean set to called program without locals
            currentvars = new ConditionVariables(startvars); // copy of.. we can modify to hearts content

            List<Action> psteps = new List<Action>();
            Action ac;
            for (int i = 0; (ac = r.GetStep(i)) != null; i++)
                psteps.Add(Action.CreateCopy(ac));

            programsteps = psteps;
        }

        #region Exec control

        public Action GetNextStep()
        {
            if (nextstepnumber < Count)
                return programsteps[nextstepnumber++];
            else
                return null;
        }

        public string Location { get { return actionfile.name + "::" + Name + " Step " + nextstepnumber; } }

        public int ExecLevel { get { return execlevel; } }

        public bool IsProgramFinished { get { return nextstepnumber >= Count; } }

        public bool IsExecuteOn { get { return execstate[execlevel] == ExecState.On; } }
        public bool IsExecuteOff { get { return execstate[execlevel] == ExecState.Off; } }
        public bool IsExecutingType(Action.ActionType ty) { return exectype[execlevel] == ty; }

        public int PushPos { get { return execlooppos[execlevel]; } }
        public void CancelPushPos() { execlooppos[execlevel] = -1; }

        public int StepNumber { get { return nextstepnumber; } }
        public void Goto(int pos) { nextstepnumber = pos; }

        public bool DoExecute(Action ac)      // execute if control state
        {
            return execstate[execlevel] == ExecState.On || ac.Type != Action.ActionType.Cmd;
        }

        public void PushState(Action.ActionType ty, bool res, bool pushpos = false)
        {
            PushState(ty, res ? ExecState.On : ExecState.Off, pushpos);
        }

        public void PushState(Action.ActionType ty, ExecState ex, bool pushpos = false)
        {
            execlevel++;
            exectype[execlevel] = ty;
            execstate[execlevel] = ex;
            execlooppos[execlevel] = (pushpos) ? (nextstepnumber - 1) : -1;
        }

        public void ChangeState(bool v)
        {
            this.execstate[execlevel] = v ? ExecState.On : ExecState.Off;
        }

        public void ChangeState(ExecState ex)
        {
            this.execstate[execlevel] = ex;
        }

        public void RemoveLevel()
        {
            execlevel = Math.Max(execlevel - 1, 0);
        }

        // true is reported on an error, or we need to get the next action.

        public bool LevelUp(int up, Action action)      // action may be null at end of program
        {
            while (up-- > 0)
            {
                if (IsExecutingType(Action.ActionType.Do))                // DO needs a while at level -1..
                {
                    if (action != null && action.Type == Action.ActionType.While)
                    {
                        if (action.LevelUp == 1)                // only 1, otherwise its incorrectly nested
                        {
                            ActionWhile w = action as ActionWhile;
                            if (w.ExecuteEndDo(this))    // if this indicates (due to true) we need to fetch next instruction
                            {
                                return true;
                            }
                            else
                                RemoveLevel();      // else, just remove level.. 
                        }
                        else
                        {
                            ReportError("While incorrectly nested under Do");
                            return true;
                        }
                    }
                    else
                    {
                        ReportError("While missing after Do");
                        return true;
                    }
                }
                else if (IsExecutingType(Action.ActionType.Loop))        // loop, when needs to make a decision if to change back pos..
                {
                    if (IsExecuteOn)          // if executing, the loop is active.. If not, we can just continue on.
                    {
                        ActionLoop l = GetStep(PushPos) as ActionLoop;  // go back and get the Loop position
                        if (l.ExecuteEndLoop(this))      // if true, it wants to move back, so go back and get next value.
                        {
                            return true;
                        }
                        else
                            RemoveLevel();      // else, just remove level.. 
                    }
                }
                else
                {                                               // normal, just see if need to loop back
                    int stepback = PushPos;

                    RemoveLevel();

                    if (stepback >= 0)
                    {
                        Goto(stepback);
                        return true;
                    }
                }
            }

            return false;
        }

        public void ResumeAfterPause()          // used when async..
        {
            System.Diagnostics.Debug.WriteLine((Environment.TickCount % 10000).ToString("00000") + " Resume code " + this.name);
            actionrun.ResumeAfterPause();
        }

        #endregion

        #region Run time errors
        public void ReportError(string s)
        {
            if (errlist != null)
                errlist += Environment.NewLine;
            errlist += s;
        }

        public string GetErrorList { get { return errlist; } }

        #endregion

    }
}