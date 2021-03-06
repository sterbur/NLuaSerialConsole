﻿/**
 * Copyright Russell Haley 2020. All Rights Reserved.
 * 
 * 
 * 
 * 
 **/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using log4net;
using NLua;
using RJCP.IO.Ports;

namespace NLuaSerialConsole
{
    public class LuaConsole
    {
        private Lua L;
        private Lua _scriptL;
        private bool _scriptRunning;
        private SerialPortStream _src;
        private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        static string SettingsFile = "settings.lua";
        private const string PROCESS_CONSOLE_CMD = ">";
        private const string PROCESS_LUA = "!";
        private const string PROCESS_LUA_PRINT = "?";
        private const string PROCESS_BUFFER_INPUT = "+";
        private const string PROCESS_END_BUFFER = "=";
        private string _lineEnding = "\r\n";
        private string _lastMessage = "";
        private bool _logging;
        private StreamWriter _scriptLog;
        private bool _binary = false;
        private byte[] _readBuffer = new byte[8192];

        public LuaConsole()
        {
            L = NewEnv();
            _src = new SerialPortStream();

            _src.DataReceived += (s, e) =>
            {
                int bytes = ((SerialPortStream)s).Read(_readBuffer, 0, _readBuffer.Length);
                byte[] buf = new byte[bytes];
                Buffer.BlockCopy(_readBuffer, 0, buf, 0, bytes);
                DefaultProcessReceived(buf);
            };
            _src.ErrorReceived += (s, e) =>
            {
                WriteConsole($"===> EventType: {e.EventType}");
            };
        }

        public void AddReceiveHandler(EventHandler<RJCP.IO.Ports.SerialDataReceivedEventArgs> handler)
        {
            _src.DataReceived += handler;
        }

        public void RemoveReceiveHandler(EventHandler<RJCP.IO.Ports.SerialDataReceivedEventArgs> handler)
        {
            _src.DataReceived -= handler;
        }

        private LuaFunction _luaDataReceiveHandler;
        private string _luaDataReceiveHandlerName;

        public void RegisterLuaHandler(string functionName)
        {
            _luaDataReceiveHandler = _scriptL[functionName] as LuaFunction;
            if(_luaDataReceiveHandler == null)
            {
                throw new Exception($"Failed to find function {functionName} Lua DataReceiveHandler");
            }
        }

        public void RemoveLuaHandler()
        {
            _luaDataReceiveHandler = null;
        }

        private void DefaultProcessReceived(byte[] buf)
        {
            ///TODO - I will need to add a queue here to separate the 
            ///line processing from processing the byte buffer.
                string str = "";
            if (_binary)
            {

                for (int i = 0; i < buf.Length; i++)
                {
                    str += string.Format("{0:X} ", buf[i]);
                }
            }
            else
            {
                str = Encoding.ASCII.GetString(buf);

                ///2020-07-13: RH - This was done to counter the echo of characers when interfacing 
                ///with a linux shell. It may be entirely unnecessary If I can turn off echo. Regardless, 
                ///When using nluaserial console with an embedded device (current use case), it is unnecessary.
                ///AND, this implementation was a terrible idea.

                //int len = _lastMessage.Length;
                ////This is to stip off whatever the user typed. It's a terrible idea.
                //if (!string.IsNullOrEmpty(_lastMessage) && str.Length >= len && str.Substring(0, len) == _lastMessage)
                //{
                //    str = str.Substring(len);
                //}
            }
                WriteConsole(str);
            if(_luaDataReceiveHandler != null)
            {
                try
                {
                    //string bufstr = Encoding.ASCII.GetString(buf);
                    _luaDataReceiveHandler.Call(buf);
                }
                catch(Exception ex)
                {
                    Log.Error(ex.Message, ex);
                }
            }
        }

        public string readFile(string filepath)
        {
            string output = System.IO.File.ReadAllText(filepath);
            return output;
        }

        private Lua NewEnv()
        {
            Lua env = new Lua();
            env["SetBinary"] = new Action<bool>(SetBinary);
            env["WriteConsole"] = new Action<string>(WriteConsole);
            env["print"] = new Action<string>(WriteConsole);
            env["ReadConsole"] = new Func<string>(ReadConsole);
            env["Send"] = new Action<string, bool>(Send);
            env["SendBinary"] = new Action<byte[]>(Send);
            env["Script"] = new Action<string>(Script);
            env["EndScript"] = new Action(EndScript);
            env["OpenPort"] = new Action<string>(OpenPort2);
            env["Open"] = new Action(OpenPort);
            env["ClosePort"] = new Action(ClosePort);
            env["Show"] = new Action<string>(Show);
            env["IsOpen"] = new Func<bool>(() => _src.IsOpen);
            env["GetPort"] = new Func<string>(() => _src.PortName);            
            env["SetPort"] = new Action<string>((portname) => _src.PortName = portname);
            env["GetSettings"] = new Func<string>(() => "Not Implemented");
            env["WireUp"] = new Action<string>(RegisterLuaHandler);
            env["Unhook"] = new Action(RemoveLuaHandler);
            env["Log"] = Log;
            return env;
        }

        private void SetBinary(bool isBinary)
        {            
            _binary = isBinary;
            WriteConsole($"SetBinary = {isBinary}");
        }

        private void EndScript()
        {
            _scriptL["RUNNING__"] = false;
            System.Threading.Thread.Sleep(500);
            _scriptL.Close();
            _scriptL.Dispose();
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
        }
        /// <summary>
        /// Run a Lua file.
        /// </summary>
        /// <param name="args"></param>
        private void RunFile(string file)
        {
            try
            {
                string scriptDir = (string)L["settings.script_path"];
                file = file.Replace("\"", "");
                _scriptL = NewEnv();
                //TODO: Check for relative path and append script_path if it's available
                //HACK...
                Directory.SetCurrentDirectory(scriptDir);
                System.IO.FileInfo f = new System.IO.FileInfo(file);
                if (f.Exists && f.Length > 0)
                {
                    _scriptL["RUNNING__"] = true;
                    _scriptL.DoFile(file);
                    EndScript();
                }
                else
                {
                    string warning = string.Format("File Not Found: {0}\r\n**Script Directory is \"{1}\"\r\n", file, scriptDir);
                    Log.WarnFormat(warning);
                    EndScript();
                }
            }
            catch(Exception ex)
            {
                Log.Error(ex.Message, ex);
                if (_logging) { Script("close"); }
                EndScript();
            }

        }

        private void LoadSettings()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFile);
                L.DoFile(path);
            }
            catch(Exception ex)
            {
                WriteConsole(ex.Message);
            }
        }

        public void Main(string[] args)
        {
            
            LoadSettings();
            try
            {
                _src.PortName = (string)L["settings.serial_port.com_port"];
                _src.BaudRate = Convert.ToInt32(L["settings.serial_port.baud_rate"]);
                string le = (string)L["settings.line_ending"];
                if (le == "unix")
                {
                    _lineEnding = "\n";
                }
                else if(le == "windows")
                {
                    _lineEnding = "\r\n";
                }

            }
            catch(Exception ex)
            {
                Log.Warn("Failed to load com settings:");
                Log.Warn(ex.Message);
            }
            Log.Info(string.Format("Starting {0}.", L["settings.console_name"]));

            string input;
            bool running = true;
            bool bufferMode = false;
            StringBuilder buffer = new StringBuilder();
            while (running)
            {
                try
                {
                    if (bufferMode)
                    {
                        Console.Write(PROCESS_LUA);
                    }
                    input = ReadConsole();
                    if (input.Length > 0)
                    {
                        if (bufferMode)
                        {
                            input = PROCESS_LUA + input;
                        }
                        if (input == PROCESS_CONSOLE_CMD + "q")
                        {
                            running = false;                            
                            if(_scriptL != null) { _scriptL.Close(); _scriptL.Dispose(); }
                            ClosePort();
                            Log.Info("Exiting Application...");
                            continue;
                        }
                        else if (input == PROCESS_CONSOLE_CMD + "d")
                        {
                            if (_scriptL != null)
                            {
                                EndScript();
                            }
                            continue;
                        }
                        else
                        {

                            switch (input.Substring(0, 1))
                            {
                                //Make a lua call
                                case PROCESS_LUA:
                                    input = input.Substring(1);
                                    if (input.Substring(input.Length - 1) == PROCESS_BUFFER_INPUT)
                                    {
                                        bufferMode = true;
                                        input = input.Substring(0, input.Length - 1);
                                        buffer.AppendLine(input);
                                    }
                                    else if (bufferMode)
                                    {
                                        if (input.Substring(input.Length - 1) == PROCESS_END_BUFFER)
                                        {
                                            buffer.Append(input.Substring(0, input.Length - 1));
                                            input = buffer.ToString();
                                            buffer.Clear();
                                            bufferMode = false;
                                        }
                                        else
                                        {
                                            buffer.AppendLine(input.Substring(0));
                                        }
                                    }

                                    if (!bufferMode)
                                    {
                                        L.DoString(input);
                                    }

                                    break;
                                case PROCESS_CONSOLE_CMD: /*Raw write to the serial port if it's open*/
                                    input = input.Substring(1).Trim();
                                    string[] cmds = input.Split(' ');

                                    switch (cmds[0].ToLower())
                                    {
                                        case "close":                                            
                                             ClosePort();                                             
                                            break;
                                        case "open":
                                            if (cmds.Length == 2)
                                                OpenPort2(cmds[1].ToLower());
                                            else
                                                OpenPort();
                                            break;
                                        case "run":
                                            if (cmds.Length == 2)
                                            {
                                                if (_scriptL == null)
                                                {
                                                    System.Threading.ThreadPool.QueueUserWorkItem(delegate
                                                    {                                                        
                                                        RunFile(cmds[1].ToLower());
                                                    }, null);
                                                }
                                                else
                                                {
                                                    Log.Warn("Script already running. >d to close it.");
                                                }
                                            }
                                            else
                                                WriteConsole(readFile("Help.txt"));
                                            break;
                                        case "end":
                                            EndScript();
                                            break;
                                        case "load":
                                            if (cmds.Length == 2 && cmds[1].ToLower() == "settings")
                                                LoadSettings();
                                            else
                                                WriteConsole(readFile("Help.txt"));
                                            //configurations
                                            break;
                                        case "script":
                                            if (cmds.Length == 2)
                                                Script(cmds[1].ToLower());
                                            else
                                                WriteConsole(readFile("Help.txt"));
                                            break;
                                        case "show":
                                            if (cmds.Length == 2)
                                                Show(cmds[1].ToLower());
                                            else
                                                WriteConsole(readFile("Help.txt"));
                                            break;
                                        case "clear":
                                            Console.Clear();
                                            break;
                                        case "help":
                                            WriteConsole(readFile("Help.txt"));
                                            break;
                                        case "commands":
                                            WriteConsole(readFile("Commands.txt"));
                                            break;
                                        default:
                                            Log.WarnFormat("{0} is not a command.", input);
                                            break;
                                    }

                                    break;
                                case PROCESS_LUA_PRINT:
                                    //wrap the string in a lua print(...) statement
                                    L.DoString(string.Format("print({0})", input.Substring(1)));
                                    break;
                                default:
                                    if (_src.IsOpen)
                                    {
                                        Send(input);
                                        _lastMessage = input;
                                    }
                                    else
                                    {
                                        Log.Warn("Serial port not open.");
                                    }
                                    break;                           
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex);
                    if (ex.InnerException != null)
                    {
                        Log.Warn(ex.InnerException);
                    }
                }
            }
        }

        public void CloseScript()
        {
            _logging = false;
            if (_scriptLog != null)
            {
                _scriptLog.Close();
            }            
            string now = DateTime.Now.ToString("yyyy-MMM-dd HH:mm:ss.fff");
            WriteConsole($"-----------Closed file at {now} -----------------");
        }

        //TODO: This function needs to return a value and if the file fails to open
        //  we can cancel our script run.
        public void Script(string cmds)
        {
            //Remove quotes
            cmds = cmds.Replace("\"", string.Empty);
            if (cmds == "")
            {
                Log.Error("Please supply a filename.");
            }
            else if (cmds == "close")
            {
                CloseScript();
            }
            else
            {
                string pathCreatedWarning = "";
                string relFolderPath = Path.GetDirectoryName(cmds);
                //NOTE: This may fail if we do something with a network drive?
                string absFolderPath = Path.GetFullPath(relFolderPath);
                bool exists = System.IO.Directory.Exists(absFolderPath);
                if (!exists)
                {
                    pathCreatedWarning = $"Folder {relFolderPath} did not exist. It was created. \nThe absolute path is {absFolderPath}";
                    Log.Warn(pathCreatedWarning);
                    System.IO.Directory.CreateDirectory(absFolderPath);
                }

                // This is not an adiquite solution, but it works for the moment. 
                // none of the log4net messages get into this log. It may be better
                // to use log4net instead and toggle timesamps on and off through the appender?
                _scriptLog = new StreamWriter(cmds, true);
                _scriptLog.AutoFlush = true;
                _logging = true;
                string now = DateTime.Now.ToString("yyyy-MMM-dd HH:mm:ss.fff");
                if (pathCreatedWarning != string.Empty)
                {
                    _scriptLog.WriteLine("Warning: " + pathCreatedWarning);
                }
                string name = System.Reflection.Assembly.GetEntryAssembly().GetName().Name;
                string version = System.Reflection.Assembly.GetEntryAssembly().GetName().Version.ToString();
                _scriptLog.WriteLine($"{name} - v {version}");
                WriteConsole($"------------- Opened {cmds} at {now} -----------------");
            }
        }


        public void Show(string cmds)
        {
            //TODO: settings, cwd, 
            if (cmds == "version")
            {
                WriteConsole(System.Reflection.Assembly.GetEntryAssembly().GetName().Version.ToString()+"\n");
            }
            else if (cmds == "ports")
            {
                foreach (PortDescription desc in SerialPortStream.GetPortDescriptions())
                {
                    WriteConsole("Port Name: " + desc.Port + " Description: " +
                        ((desc.Description == string.Empty) ? "No Description provided" : desc.Description));
                }
            }
            else
            {
                WriteConsole("Show command: Show version | ports");
            }
        }

        public void OpenPort2(string cmds)
        {
            _src.PortName = cmds;
            OpenPort();
        }

        public void OpenPort()
        {
            try
            {
                if (_src.IsOpen)
                {
                    WriteConsole("Port open. Close it first.");
                    return;
                }
                _src.Open();
                Log.InfoFormat("{0} is open.\r\n", _src.PortName);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
                        
        }

        public void ClosePort()
        {
            try
            {
                if (_src.IsOpen)
                {                        
                    _src.Close();
                    WriteConsole("Serial Port Closed.");
                }
                else
                {
                    WriteConsole("Port is not open.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        public void WriteConsole(string data)
        {
            try
            {
                //TODO Check if file is open. on error, this can esplode!
                if (_logging)
                {
                    _scriptLog.Write(data);
                }
                Console.Write(data);
            }
            catch(Exception ex)
            {
                Log.Error(ex.Message);
            }
        }

        public string ReadConsole()
        {
            string dataIn = Console.ReadLine();
            if(_logging)
            {
                _scriptLog.Write(dataIn);
            }
            return dataIn;
        }

        public void Send(string data, bool appendLineEnding = true)
        {
            if(_logging)
            {
                _scriptLog.Write(data);
            }
            data = appendLineEnding ? data + _lineEnding : data;
            _src.Write(data);
        }

        public void Send(byte[] buffer)
        {
            if (buffer != null)
            {
                if (_logging)
                {
                    //This is a cheat and a hacck but...
                    _scriptLog.Write(Encoding.ASCII.GetString(buffer));
                }
                _src.Write(buffer, 0, buffer.Length);
            }
            else
            {
                Log.Error("Buffer was null");
            }
        }
    }
}
