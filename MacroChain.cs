using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Logging;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace MacroChain {
    public sealed unsafe class MacroChain : IDalamudPlugin {

        // Plugin services
        [PluginService] public static CommandManager CommandManager { get; private set; } = null!;
        [PluginService] public static Framework Framework { get; private set; } = null!;

        [PluginService] public static ClientState ClientState { get; private set; } = null!;
        [PluginService] public static ChatGui Chat { get; private set; } = null!;

        public string Name => "Macro Chain";

        private delegate void MacroCallDelegate(RaptureShellModule* raptureShellModule, RaptureMacroModule.Macro* macro);

        private Hook<MacroCallDelegate> macroCallHook;
        
        public MacroChain() {
            // Set up hook and command handlers
            macroCallHook = Hook<MacroCallDelegate>.FromAddress(new IntPtr(RaptureShellModule.MemberFunctionPointers.ExecuteMacro), MacroCallDetour);
            macroCallHook?.Enable();

            CommandManager.AddHandler("/nextmacro", new Dalamud.Game.Command.CommandInfo(OnMacroCommandHandler) {
                HelpMessage = "Executes the next macro. Add number to to specify which macro to run next",
                ShowInHelp = true
            });
            CommandManager.AddHandler("/runmacro", new Dalamud.Game.Command.CommandInfo(OnRunMacroCommand) {
                HelpMessage = "Execute a macro (Not usable inside macros). - /runmacro ## [individual|shared].",
                ShowInHelp = true
            });

            Framework.Update += FrameworkUpdate;
        }

        // Clean up hook and command handlers
        public void Dispose() {
            CommandManager.RemoveHandler("/nextmacro");
            CommandManager.RemoveHandler("/runmacro");
            macroCallHook?.Disable();
            macroCallHook?.Dispose();
            macroCallHook = null;
            Framework.Update -= FrameworkUpdate;
        }

        private RaptureMacroModule.Macro* lastExecutedMacro = null;
        private RaptureMacroModule.Macro* nextMacro = null;
        private RaptureMacroModule.Macro* downMacro = null;
        private RaptureMacroModule.Macro* specificMacro = null;
        private readonly Stopwatch paddingStopwatch = new Stopwatch();

        private void MacroCallDetour(RaptureShellModule* raptureShellModule, RaptureMacroModule.Macro* macro) {
            macroCallHook?.Original(raptureShellModule, macro);
            // Check if macro is locked
            if (RaptureShellModule.Instance->MacroLocked) return;

            // Update executed macro and get the next and down macro values
            lastExecutedMacro = macro;
            nextMacro = null;
            downMacro = null;
            specificMacro = null;

            //ignore specific macros
            if (lastExecutedMacro == RaptureMacroModule.Instance->Individual[99] || lastExecutedMacro == RaptureMacroModule.Instance->Shared[99]) {
                return;
            }

            nextMacro = macro + 1;
            for (var i = 90; i < 100; i++) {
                if (lastExecutedMacro == RaptureMacroModule.Instance->Individual[i] || lastExecutedMacro == RaptureMacroModule.Instance->Shared[i]) {
                    return;
                }
            }

            downMacro = macro + 10;
            specificMacro = macro;
        }
        
        public void OnMacroCommandHandler(string command, string args) {
            try {
                if (lastExecutedMacro == null) {
                    Chat.PrintError("No macro is running.");
                    return;
                }
                
                if (args.ToLower() == "down") 
                {
                    if (downMacro != null) {
                        RaptureShellModule.Instance->MacroLocked = false;
                        RaptureShellModule.Instance->ExecuteMacro(downMacro);
                    } else
                        Chat.PrintError("Can't use `/nextmacro down` on macro 90+");
                } 
                else 
                {
                    if (!(string.IsNullOrWhiteSpace(args)))
                    {   
                        if (int.TryParse(args, out int num) || num >=0 || num <= 99 || specificMacro != null)
                        {
                            RaptureShellModule.Instance->MacroLocked = false;
                            RaptureShellModule.Instance->ExecuteMacro(specificMacro);                        
                        } else
                            Chat.PrintError("Number must be between [0;99]");
                    }

                    else
                    {
                        if (nextMacro != null) {
                            RaptureShellModule.Instance->MacroLocked = false;
                            RaptureShellModule.Instance->ExecuteMacro(nextMacro);
                        } else
                            Chat.PrintError("Can't use `/nextmacro` on macro 99.");
                        }
                }
                    
              RaptureShellModule.Instance->MacroLocked = false;
                
            } catch (Exception ex) {
                PluginLog.LogError(ex.ToString());
            }
        }

        // Check that the macro is running and handle padding
        public void FrameworkUpdate(Framework framework) {
            if (lastExecutedMacro == null) return;
            if (ClientState == null) return;
            if (!ClientState.IsLoggedIn) {
                lastExecutedMacro = null;
                paddingStopwatch.Stop();
                paddingStopwatch.Reset();
                return;
            }
            if (RaptureShellModule.Instance->MacroCurrentLine >= 0) {
                paddingStopwatch.Restart();
                return;
            }

            if (paddingStopwatch.ElapsedMilliseconds > 2000) {
                lastExecutedMacro = null;
                paddingStopwatch.Stop();
                paddingStopwatch.Reset();
            }
        }

        public void OnRunMacroCommand(string command, string args) {
            try {
                if (lastExecutedMacro == null) {
                    Chat.PrintError("/runmacro is not usable while macros are running. Please use /nextmacro");
                    return;
                }
                var argSplit = args.Split(' ');
                var num = byte.Parse(argSplit[0]);

                if (num > 99) {
                    Chat.PrintError("Invalid Macro number.\nShould be 0 - 99");
                    return;
                }

                var shared = false;
                foreach (var arg in argSplit.Skip(1)) {
                    switch (arg.ToLower()) {
                        case "shared":
                        case "share":
                        case "s": {
                            shared = true;
                            break;
                        }
                        case "individual":
                        case "i": {
                            shared = false;
                            break;
                        }
                    }
                }
                RaptureShellModule.Instance->ExecuteMacro((shared ? RaptureMacroModule.Instance->Shared : RaptureMacroModule.Instance->Individual)[num]);
            } catch (Exception ex) {
                PluginLog.LogError(ex.ToString());
            }
        }
    }
}
