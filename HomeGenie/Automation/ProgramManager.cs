/*
    This file is part of HomeGenie Project source code.

    HomeGenie is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    HomeGenie is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with HomeGenie.  If not, see <http://www.gnu.org/licenses/>.  
*/

/*
*     Author: Generoso Martello <gene@homegenie.it>
*     Project Homepage: http://homegenie.it
*/

using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using HomeGenie.Automation.Scripting;
using HomeGenie.Data;
using HomeGenie.Service;
using MIG;
using HomeGenie.Service.Constants;
using HomeGenie.Automation.Scheduler;
using Microsoft.Scripting.Hosting;
using Newtonsoft.Json;
using System.Globalization;
using System.Diagnostics;

namespace HomeGenie.Automation
{

    public class ScriptEngineErrors : ErrorListener
    {
        private string blockType = "TC";
        public List<ProgramError> Errors = new List<ProgramError>();

        public ScriptEngineErrors(string type)
        {
            blockType = type;
        }

        public override void ErrorReported(ScriptSource source, string message, Microsoft.Scripting.SourceSpan span, int errorCode, Microsoft.Scripting.Severity severity)
        {
            Errors.Add(new ProgramError() {
                Line = span.Start.Line,
                Column = span.Start.Column,
                ErrorMessage = message,
                ErrorNumber = errorCode.ToString(),
                CodeBlock = blockType
            });
        }
    }

    public class ProgramManager
    {
        private TsList<ProgramBlock> automationPrograms = new TsList<ProgramBlock>();
        private HomeGenieService homegenie = null;
        private SchedulerService scheduler = null;
        private MacroRecorder macroRecorder = null;

        public class RoutedEvent
        {
            public object Sender;
            public Module Module;
            public ModuleParameter Parameter;
        }

        //private object lockObject = new object();
        private bool isEngineRunning = true;
        private bool isEngineEnabled = false;
        public static int USER_SPACE_PROGRAMS_START = 1000;

        public ProgramManager(HomeGenieService hg)
        {
            homegenie = hg;
            macroRecorder = new MacroRecorder(this);
            scheduler = new SchedulerService(this);
            scheduler.Start();
        }

        public bool Enabled
        {
            get { return isEngineEnabled; }
            set { isEngineEnabled = value; }
        }

        public HomeGenieService HomeGenie
        {
            get { return homegenie; }
        }

        public MacroRecorder MacroRecorder
        {
            get { return macroRecorder; }
        }

        public SchedulerService SchedulerService
        {
            get { return scheduler; }
        }

        public List<ProgramError> CompileScript(ProgramBlock program)
        {
            return program.Compile();
        }

        // TODO: v1.1 !!!IMPORTANT!!! move thread allocation and starting to ProgramEngineBase.cs class
        public void Run(ProgramBlock program, string options)
        {
            if (program.IsRunning)
                return;

            if (program.Engine.ProgramThread != null)
            {
                program.Engine.Stop();
                program.IsRunning = false;
            }

            program.IsRunning = true;
            RaiseProgramModuleEvent(program, Properties.ProgramStatus, "Running");

            program.TriggerTime = DateTime.UtcNow;

            program.Engine.ProgramThread = new Thread(() =>
            {
                try
                {
                    MethodRunResult result = null;
                    try
                    {
                        result = program.Run(options);
                    }
                    catch (Exception ex)
                    {
                        result = new MethodRunResult();
                        result.Exception = ex;
                    }
                    program.IsRunning = false;
                    if (result != null && result.Exception != null && !result.Exception.GetType().Equals(typeof(System.Reflection.TargetException)))
                    {
                        // runtime error occurred, script is being disabled
                        // so user can notice and fix it
                        List<ProgramError> error = new List<ProgramError>() { program.GetFormattedError(result.Exception, false) };
                        program.ScriptErrors = JsonConvert.SerializeObject(error);
                        program.IsEnabled = false;
                        RaiseProgramModuleEvent(program, Properties.RuntimeError, "CR: " + result.Exception.Message.Replace('\n', ' ').Replace('\r', ' '));
                    }
                    RaiseProgramModuleEvent(program, Properties.ProgramStatus, "Idle");
                }
                catch (ThreadAbortException e)
                {
                    program.IsRunning = false;
                    RaiseProgramModuleEvent(program, Properties.ProgramStatus, "Interrupted");
                }
                finally
                {
                    program.Engine.ProgramThread = null;
                }
            });

            if (program.ConditionType == ConditionType.Once)
            {
                program.IsEnabled = false;
            }

            try
            {
                program.Engine.ProgramThread.Start();
            }
            catch
            {
                program.Engine.Stop();
                program.IsRunning = false;
                RaiseProgramModuleEvent(program, Properties.ProgramStatus, "Idle");
            }
        }

        public void StopEngine()
        {
            isEngineRunning = false;
            scheduler.Stop();
            foreach (ProgramBlock program in automationPrograms)
            {
                program.Engine.Stop();
            }
        }

        public TsList<ProgramBlock> Programs { get { return automationPrograms; } }

        public int GeneratePid()
        {
            int pid = USER_SPACE_PROGRAMS_START;
            foreach (ProgramBlock program in automationPrograms)
            {
                if (pid <= program.Address)
                    pid = program.Address + 1;
            }
            return pid;
        }

        public void ProgramAdd(ProgramBlock program)
        {
            automationPrograms.Add(program);
            program.EnabledStateChanged += program_EnabledStateChanged;
            program.Engine.SetHost(homegenie);
            // Initialize state
            RaiseProgramModuleEvent(program, Properties.ProgramStatus, "Idle");
            if (program.IsEnabled)
            {
                StartProgramScheduler(program);
            }
        }

        public void ProgramRemove(ProgramBlock program)
        {
            program.IsEnabled = false;
            program.Engine.Stop();
            automationPrograms.Remove(program);
            // delete program files
            string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "programs");
            // remove csharp assembly
            try
            {
                File.Delete(Path.Combine(file, program.Address + ".dll"));
            }
            catch
            {
            }
            // remove arduino folder files 
            try
            {
                Directory.Delete(Path.Combine(file, "arduino", program.Address.ToString()), true);
            } catch { }
        }

        internal void RaiseProgramModuleEvent(ProgramBlock program, string property, string value)
        {
            var programModule = homegenie.Modules.Find(m => m.Domain == Domains.HomeAutomation_HomeGenie_Automation && m.Address == program.Address.ToString());
            if (programModule != null)
            {
                Utility.ModuleParameterSet(programModule, property, value);
                homegenie.RaiseEvent(program.Address, programModule.Domain, programModule.Address, "Automation Program", property, value);
                //homegenie.MigService.RaiseEvent(actionEvent);
                //homegenie.SignalModulePropertyChange(this, programModule, actionEvent);
            }
        }

        #region Event Propagation


        #region Module/Interface Events handling and propagation

        public void SignalPropertyChange(object sender, Module module, MigEvent eventData)
        {
            ModuleParameter parameter = Utility.ModuleParameterGet(module, eventData.Property);

            var routedEvent = new RoutedEvent() {
                Sender = sender,
                Module = module,
                Parameter = parameter
            };

            // Route event to Programs->ModuleIsChangingHandler
            if (RoutePropertyBeforeChangeEvent(routedEvent))
            {
                // Route event to Programs->ModuleChangedHandler
                ThreadPool.QueueUserWorkItem(new WaitCallback(RoutePropertyChangedEvent), routedEvent);
            }
        }

        public bool RoutePropertyBeforeChangeEvent(object eventData)
        {
            var moduleEvent = (RoutedEvent)eventData;
            var moduleHelper = new Automation.Scripting.ModuleHelper(homegenie, moduleEvent.Module);
            string originalValue = moduleEvent.Parameter.Value;
            for (int p = 0; p < Programs.Count; p++)
            {
                var program = Programs[p];
                if (!program.IsEnabled) continue;
                if ((moduleEvent.Sender == null || !moduleEvent.Sender.Equals(program.Address)))
                {
                    if (program.Engine.ModuleIsChangingHandler != null)
                    {
                        bool handled = !program.Engine.ModuleIsChangingHandler(moduleHelper, moduleEvent.Parameter);
                        if (handled)
                        {
                            // stop routing event if "false" is returned
                            MigService.Log.Debug("Event propagation halted by automation program '{0}' ({1}) (Name={2}, OldValue={3}, NewValue={4})", program.Name, program.Address, moduleEvent.Parameter.Name, originalValue, moduleEvent.Parameter.Value);
                            return false;
                        }
                        else  if (moduleEvent.Parameter.Value != originalValue)
                        {
                            // If manipulated, the event is not routed anymore.
                            MigService.Log.Debug("Event propagation halted - parameter manipulated by automation program '{0}' ({1}) (Name={2}, OldValue={3}, NewValue={4})", program.Name, program.Address, moduleEvent.Parameter.Name, originalValue, moduleEvent.Parameter.Value);
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        public void RoutePropertyChangedEvent(object eventData)
        {
            var moduleEvent = (RoutedEvent)eventData;
            var moduleHelper = new Automation.Scripting.ModuleHelper(homegenie, moduleEvent.Module);
            string originalValue = moduleEvent.Parameter.Value;
            for (int p = 0; p < Programs.Count; p++)
            {
                var program = Programs[p];
                if (program == null || !program.IsEnabled || !isEngineEnabled || !isEngineRunning) continue;
                if ((moduleEvent.Sender == null || !moduleEvent.Sender.Equals(program)))
                {
                    try
                    {
                        if (!program.IsRunning)
                        Utility.RunAsyncTask(() =>
                        {
                            if (WillProgramRun(program))
                            {
                                Run(program, null);
                            }
                        });

                        if (program.Engine.ModuleChangedHandler != null && moduleEvent.Parameter != null) // && proceed)
                        {
                            bool handled = !program.Engine.ModuleChangedHandler(moduleHelper, moduleEvent.Parameter);
                            if (handled)
                            {
                                // stop routing event if "false" is returned
                                MigService.Log.Debug("Event propagation halted by automation program '{0}' ({1}) (Name={2}, OldValue={3}, NewValue={4})", program.Name, program.Address, moduleEvent.Parameter.Name, originalValue, moduleEvent.Parameter.Value);
                                break;
                            }
                            else if (moduleEvent.Parameter.Value != originalValue)
                            {
                                // If manipulated, the event is not routed anymore.
                                MigService.Log.Debug("Event propagation halted - parameter manipulated by automation program '{0}' ({1}) (Name={2}, OldValue={3}, NewValue={4})", program.Name, program.Address, moduleEvent.Parameter.Name, originalValue, moduleEvent.Parameter.Value);
                                break;
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        HomeGenieService.LogError(
                            program.Domain,
                            program.Address.ToString(),
                            ex.Message,
                            "Exception.StackTrace",
                            ex.StackTrace
                        );
                    }
                }
            }
        }

        #endregion

        #region Automation Programs Dynamic API 

        //TODO: should the following 3 methods moved to ProgramEngine?
        public void RegisterDynamicApi(string apiCall, Func<object, object> handler)
        {
            ProgramDynamiApi.Register(apiCall, handler);
        }

        public void UnRegisterDynamicApi(string apiCall)
        {
            ProgramDynamiApi.UnRegister(apiCall);
        }

        public object TryDynamicApi(MigInterfaceCommand command)
        {
            object response = "";
            // Dynamic Interface API 
            var registeredApi = command.Domain + "/" + command.Address + "/" + command.Command;
            var handler = ProgramDynamiApi.Find(registeredApi);
            if (handler != null)
            {
                // explicit command API handlers registered in the form <domain>/<address>/<command>
                // receives only the remaining part of the request after the <command>
                var args = command.OriginalRequest.Substring(registeredApi.Length).Trim('/');
                response = handler(args);
            }
            else
            {
                handler = ProgramDynamiApi.FindMatching(command.OriginalRequest.Trim('/'));
                if (handler != null)
                {
                    // other command API handlers
                    // receives the full request string
                    response = handler(command.OriginalRequest.Trim('/'));
                }
            }
            return response;
        }

        #endregion

        #endregion

        private void StartProgramScheduler(ProgramBlock program)
        {
            StopProgramScheduler(program);
            program.Engine.StartupThread = new Thread(CheckProgramSchedule);
            program.Engine.StartupThread.Start(program);
        }
        private void StopProgramScheduler(ProgramBlock program)
        {
            if (program.Engine.StartupThread != null)
            {
                try
                {
                    if (!program.Engine.StartupThread.Join(1000))
                        program.Engine.StartupThread.Abort();
                } catch { }
                program.Engine.StartupThread = null;
            }
        }

        // this will ensure that the StartupCode is run at least every minute for checking scheduler conditions if any
        private void CheckProgramSchedule(object p)
        {
            ProgramBlock program = (ProgramBlock)p;
            while (isEngineRunning && program.IsEnabled)
            {
                Thread.Sleep((60 - DateTime.Now.Second) * 1000);
                // the startup code is not evaluated while the program is running
                if (program.IsRunning || !program.IsEnabled || !isEngineEnabled)
                {
                    continue;
                }
                else if (WillProgramRun(program))
                {
                    Run(program, null);
                }
            }
        }

        private bool WillProgramRun(ProgramBlock program)
        {
            bool isConditionSatisfied = false;
            // evaluate and get result from the code
            lock (program.OperationLock)
            try
            {
                program.WillRun = false;
                //
                var result = program.EvaluateCondition();
                if (result != null && result.Exception != null)
                {
                    // runtime error occurred, script is being disabled
                    // so user can notice and fix it
                    List<ProgramError> error = new List<ProgramError>() { program.GetFormattedError(result.Exception, true) };
                    program.ScriptErrors = JsonConvert.SerializeObject(error);
                    program.IsEnabled = false;
                    RaiseProgramModuleEvent(program, Properties.RuntimeError, "TC: " + result.Exception.Message.Replace('\n', ' ').Replace('\r', ' '));
                }
                else
                {
                    isConditionSatisfied = (result != null ? (bool)result.ReturnValue : false);
                }
                //
                bool lastResult = program.LastConditionEvaluationResult;
                program.LastConditionEvaluationResult = isConditionSatisfied;
                //
                if (program.ConditionType == ConditionType.OnSwitchTrue)
                {
                    isConditionSatisfied = (isConditionSatisfied == true && isConditionSatisfied != lastResult);
                }
                else if (program.ConditionType == ConditionType.OnSwitchFalse)
                {
                    isConditionSatisfied = (isConditionSatisfied == false && isConditionSatisfied != lastResult);
                }
                else if (program.ConditionType == ConditionType.OnTrue || program.ConditionType == ConditionType.Once)
                {
                    // noop
                }
                else if (program.ConditionType == ConditionType.OnFalse)
                {
                    isConditionSatisfied = !isConditionSatisfied;
                }
            }
            catch (Exception ex)
            {
                // a runtime error occured
                if (!ex.GetType().Equals(typeof(System.Reflection.TargetException)))
                {
                    List<ProgramError> error = new List<ProgramError>() { program.GetFormattedError(ex, true) };
                    program.ScriptErrors = JsonConvert.SerializeObject(error);
                    program.IsEnabled = false;
                    RaiseProgramModuleEvent(program, Properties.RuntimeError, "TC: " + ex.Message.Replace('\n', ' ').Replace('\r', ' '));
                }
            }
            return isConditionSatisfied && program.IsEnabled;
        }

        private void program_EnabledStateChanged(object sender, bool isEnabled)
        {
            ProgramBlock program = (ProgramBlock)sender;
            if (isEnabled)
            {
                program.ScriptErrors = "";
                homegenie.modules_RefreshPrograms();
                homegenie.modules_RefreshVirtualModules();
                RaiseProgramModuleEvent(program, Properties.ProgramStatus, "Enabled");
                StartProgramScheduler(program);
            }
            else
            {
                StopProgramScheduler(program);
                RaiseProgramModuleEvent(program, Properties.ProgramStatus, "Disabled");
                homegenie.modules_RefreshPrograms();
                homegenie.modules_RefreshVirtualModules();
            }
            homegenie.modules_Sort();
        }
    }
}