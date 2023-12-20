using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text;
using System.Threading.Tasks;
using ConsoleGameEngine;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using WindowsInput.Native;
using WindowsInput;

namespace ConsoleCollision
{
    /// <summary>
    /// Contains native methods imported as unmanaged code.
    /// </summary>
    internal static class DllImports
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct COORD
        {

            public short X;
            public short Y;
            public COORD(short x, short y)
            {
                this.X = x;
                this.Y = y;
            }

        }
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetStdHandle(int handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetConsoleDisplayMode(IntPtr ConsoleOutput, uint Flags, out COORD NewScreenBufferDimensions);
    }
    class Program
    {
        private static Timer tmrDraw = null;
        private static Timer tmrTest = null;
        private static Random rnd = new Random();
        private static int RefreshRate = 8; //24 our main timer draw frequency
        //Consolas @ 24pt: Width = 120, Height = 39
        //private static int ScreenWidth = Console.WindowWidth; //120;
        private static int ScreenWidth = Console.LargestWindowWidth;
        //private static int ScreenHeight = Console.WindowHeight - 1; //39;
        private static int ScreenHeight = Console.LargestWindowHeight;
        private static char[] ScreenArray = new char[(ScreenWidth * ScreenHeight) - (ScreenHeight + 1)]; //if we don't subtract the extra +1 then the cursor will move to the next line and screen will jump up & down
        private readonly static object conLock = new object();
        private static int UpdateRate = 60; //only used for draw test
        private static int MAX_PARTS = 400;
        private static ConsoleColor conColor;
        private static List<Particle> parts = new List<Particle>();
        private static Stopwatch stopWatch = new Stopwatch();
        private static BackgroundWorker _bw;
        private static bool running = true;
        private static long cycles = 0;
        private static int sandLife = ScreenHeight + 1;
        private static int startOver = 500;
        private static int maxSand = 4200;
        private static int debug_x = 0;
        private static int debug_y = 0;
        private static InputSimulator sim = new InputSimulator();

        private static double fStart = 0.0;
        private static double fEnd = 0.0;




        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetConsoleWindow();
        private static IntPtr ThisConsole = GetConsoleWindow();
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int HIDE = 0;
        private const int MAXIMIZE = 3;
        private const int MINIMIZE = 6;
        private const int RESTORE = 9;

        enum PIXEL_TYPE
        {
            SOLID = 0x2588,
            THREEQUARTERS = 0x2593,
            HALF = 0x2592,
            QUARTER = 0x2591,
            BLK_SQUARE = 0x25A0,
            HOLLOW_DOT = 0x00B0,
            SOLID_DOT = 0x00B7
        }
        public class Particle
        {
            public int x_pos { get; set; }
            public int y_pos { get; set; }
            public int x_vel { get; set; }
            public int y_vel { get; set; }
            public int active { get; set; }
            public bool settle { get; set; }
            public char glyph { get; set; }
        }

        static void Main(string[] args)
        {
            try
            {
                List<string> lstArgs = new List<string>();
                Array.ForEach<string>(Environment.GetCommandLineArgs(), (o) => { lstArgs.Add(o.ToLower()); });
                if (lstArgs.Count > 0)
                {
                    int argIdx = 0;
                    foreach (string ar in lstArgs)
                    {
                        if (argIdx++ > 0)
                        {
                            switch (ar.ToLower())
                            {
                                case "saver":
                                    InitDisplay();
                                    StartScreenSaver();
                                    break;
                                case "crawl":
                                    InitDisplay();
                                    StartCrawler();
                                    break;
                                default:
                                    Console.WriteLine($"> Unknown argument: {ar}");
                                    break;
                            }
                        }
                    }
                }

#if DEBUG
                InitDisplay();
                //FillFrame();
                //ConsoleKeyInfo dbg = Console.ReadKey(true); //keep main thread alive, because background threads are not enough
                //StartDebug();
                StartSand();
#endif
                Console.Write($"[waiting for keypress]");
                while (running)
                {
                    ConsoleKeyInfo cki = Console.ReadKey(true); //keep main thread alive, because background threads are not enough
                    switch (cki.Key)
                    {
                        case ConsoleKey.Escape:
                            if ((_bw != null) && _bw.IsBusy)
                            {
                                Console.WriteLine(Environment.NewLine + "> Closing system...");
                                running = false;
                                _bw.CancelAsync(); //kill timer background worker
                            }
                            if (tmrDraw != null)
                            {
                                running = false;
                                tmrDraw?.Change(Timeout.Infinite, Timeout.Infinite); //disable timer
                                tmrDraw.Dispose();
                            }
                            break;
                        case ConsoleKey.Spacebar:
                            InitDisplay();
                            //StartCrawler();
                            StartSand();
                            break;
                        case ConsoleKey.S:
                            Console.WriteLine($"> Starting screen saver...");
                            _bw = new BackgroundWorker
                            {
                                WorkerReportsProgress = true,
                                WorkerSupportsCancellation = true
                            };
                            _bw.DoWork += bw_DoWork;
                            _bw.ProgressChanged += bw_ProgressChanged;
                            _bw.RunWorkerCompleted += bw_RunWorkerCompleted;
                            _bw.RunWorkerAsync("Screen Saver");
                            break;
                        default: //invalid key press
                            break;
                    }
                }
            }
            catch (Exception mex)
            {
                Console.WriteLine($"> ERROR: {mex.Message}");
            }
            finally
            {
                Console.CursorVisible = true;
            }
        }
        //========================================================================================================================
        static void bw_DoWork(object sender, DoWorkEventArgs e)
        {
            //while (running) //we must keep the thread alive or the system will close
            //{
            if (_bw.CancellationPending)
            {
                e.Cancel = true;
                if (tmrDraw != null)
                {
                    tmrDraw?.Change(Timeout.Infinite, Timeout.Infinite); //disable timer
                }
                return;
            }
            //_bw.ReportProgress(++totalTicks);

            if (tmrDraw == null)
            {
                StartScreenSaver();
            }
            Thread.Sleep(100); //the screen saver has it's own timer 
            //}
            e.Result = 1; // This gets passed to RunWorkerCompleted
        }

        //========================================================================================================================
        static void bw_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            /*
            if (e.Cancelled)
                Console.WriteLine("> You canceled the background worker");
            else if (e.Error != null)
                Console.WriteLine("> Worker exception: " + e.Error.ToString());
            else
            {
                Console.WriteLine("> Complete: " + e.Result);      // from DoWork
                //Environment.Exit(0); //you could close the program when background process completes
            }
            */
        }

        //========================================================================================================================
        static void bw_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            /*
            if (e.ProgressPercentage > 20 && !keyPressed) //if idle for too long start default action
            {
                keyPressed = true;
                Maximize(SW_MINIMIZE);
                Console.WriteLine(Environment.NewLine + "> Starting heartbeat timer...");
                heartbeat = new Timer(new TimerCallback(SendHeartbeat), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }
            */
        }

        static double FramesPerSecond(double seconds)
        {
            double framesPerSecond = 0.0;
            framesPerSecond = (framesPerSecond * 0.9) + (1.0 / seconds * 1.0);
            return framesPerSecond;
        }

        /// <summary>Class to get current timestamp with enough precision</summary>
        static class CurrentMillis
        {
            private static readonly DateTime Jan1St1970 = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            /// <summary>Get extra long current timestamp</summary>
            public static long Millis { get { return (long)((DateTime.UtcNow - Jan1St1970).TotalMilliseconds); } }
        }

        static void StartScreenSaver()
        {
            //Put our particles into the matrix
            Random prnd = new Random();
            for (int i = 0; i < MAX_PARTS; i++)
            {
                parts.Add(new Particle { x_pos = prnd.Next(2, ScreenWidth), y_pos = prnd.Next(2, ScreenHeight), x_vel = prnd.Next(1, 2), y_vel = prnd.Next(1, 2), active = prnd.Next(20, 100) });
            }
            /*
            int dice_roll = rnd.Next(1000);
            if (dice_roll >= 900)
                conColor = ConsoleColor.Red;
            else if (dice_roll >= 750)
                conColor = ConsoleColor.Yellow;
            else if (dice_roll >= 550)
                conColor = ConsoleColor.Green;
            else if (dice_roll >= 350)
                conColor = ConsoleColor.Cyan;
            else if (dice_roll >= 200)
                conColor = ConsoleColor.Magenta;
            else if (dice_roll >= 100)
                conColor = ConsoleColor.Blue;
            else
            */
            conColor = ConsoleColor.White;
            tmrDraw = new Timer(new TimerCallback(DrawFrame), null, TimeSpan.FromMilliseconds(RefreshRate), TimeSpan.FromMilliseconds(RefreshRate));
            //Console.ReadKey(); //pause for input so our main thread does not exit
            //tmrDraw?.Change(Timeout.Infinite, Timeout.Infinite); //disable timer while we do our stuff
            //running = false;
        }

        //========================================================================================================================
        static void StartCrawler()
        {
            //Put our particles into the matrix
            Random prnd = new Random();
            for (int i = 0; i < ScreenHeight; i++)
            {
                parts.Add(new Particle { x_pos = 0, y_pos = i, x_vel = 1, y_vel = 0, active = 20, settle = false });
            }
            tmrDraw = new Timer(new TimerCallback(DrawFrame), null, TimeSpan.FromMilliseconds(RefreshRate), TimeSpan.FromMilliseconds(RefreshRate));
        }

        //========================================================================================================================
        static void StartSand()
        {
            //Put our particles into the matrix
            parts.Add(new Particle { x_pos = ScreenWidth / 2, y_pos = 0, x_vel = 0, y_vel = 1, active = sandLife, settle = false, glyph = (char)PIXEL_TYPE.QUARTER });
            conColor = ConsoleColor.White;
            tmrDraw = new Timer(new TimerCallback(DrawFrame), null, TimeSpan.FromMilliseconds(RefreshRate), TimeSpan.FromMilliseconds(RefreshRate));
        }

        //========================================================================================================================
        static void StartDebug()
        {
            //Put our particles into the matrix
            parts.Add(new Particle { x_pos = 0, y_pos = ScreenHeight - 1, x_vel = 1, y_vel = 0, active = sandLife, settle = false, glyph = (char)PIXEL_TYPE.HALF });
            conColor = ConsoleColor.White;
            tmrDraw = new Timer(new TimerCallback(DrawFrame), null, TimeSpan.FromMilliseconds(RefreshRate), TimeSpan.FromMilliseconds(RefreshRate));
        }

        static void GoFullScreen(object state)
        {
            try
            {
                //System.Windows.Forms.SendKeys.SendWait("%{Tab}");
                // CTRL-C (effectively a copy command in many situations)

                sim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.MENU, VirtualKeyCode.RETURN);

                sim.Keyboard.KeyDown(VirtualKeyCode.MENU);
                sim.Keyboard.KeyPress(VirtualKeyCode.RETURN);
                sim.Keyboard.KeyUp(VirtualKeyCode.MENU);
            }
            catch { }
        }

        //========================================================================================================================
        public static void InitDisplay()
        {
            //DllImports.COORD xy = new DllImports.COORD(100, 100);
            //DllImports.SetConsoleDisplayMode(ThisConsole, 1, out xy); // set the console to fullscreen

            //Maximize window
            Console.SetWindowSize(Console.LargestWindowWidth, Console.LargestWindowHeight);
            ShowWindow(ThisConsole, MAXIMIZE);

            ScreenWidth = Console.LargestWindowWidth;
            ScreenHeight = Console.LargestWindowHeight;
            Console.CursorVisible = false;
            ClearFrame(); //clear our display buffer
            Console.WriteLine($"> Width: {ScreenWidth}");
            Console.WriteLine($"> Height: {ScreenHeight}");
            Console.WriteLine($"> Buffer: {ScreenArray.Length}");
            Thread.Sleep(1500);
            Console.Clear(); //clear the console to make sure we start at 0,0

            //tmrDraw = new Timer(new TimerCallback(GoFullScreen), null, TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1000));
        }
        //========================================================================================================================
        public static void Draw(int x, int y, char data)
        {
            debug_x = x;
            debug_y = y;

            lock (conLock)
            {
                try
                {

                    int location = (y * (ScreenWidth - 1)) + x;
                    if (location < ScreenArray.Length)
                    {
                        ScreenArray[location] = data;
                    }
                    else
                    {
                        ScreenArray[ScreenArray.Length - 1] = data;
                    }
                }
                catch (Exception ex)
                {
                    Console.CursorLeft = 0;
                    Console.Write($"> Draw({ScreenArray.Length}) x={debug_x},y={debug_y}: {ex.Message}");
                }
            }
        }
        //========================================================================================================================
        public static char CheckLocation(int x, int y)
        {
            lock (conLock)
            {
                int location = (y * (ScreenWidth - 1)) + x;
                if (location < ScreenArray.Length)
                {
                    return ScreenArray[location];
                }
                else
                {
                    return '\0';
                }
            }
        }
        //========================================================================================================================
        public static void ClearFrame()
        {
            lock (ScreenArray)
            {
                //for (int i = 0; i < (ScreenWidth * ScreenHeight); i++)
                //{
                //    ScreenArray[i] = ' ';
                //}

                for (int i = 0; i < ScreenArray.Length; i++)
                {
                    ScreenArray[i] = ' ';
                }
            }
        }
        //========================================================================================================================
        public static void FillFrame()
        {
            lock (ScreenArray)
            {
                //for (int i = 0; i < (ScreenWidth * ScreenHeight); i++)
                //{
                //    ScreenArray[i] = ' ';
                //}

                for (int i = 0; i < ScreenArray.Length; i++)
                {
                    ScreenArray[i] = '+';
                }
            }
        }
        //========================================================================================================================
        private static void DrawFrame(object state)
        {
            try
            {
                cycles++;

                //fStart = (double)DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0;

                /*-----------------*/
                /*  Handle timers  */
                /*-----------------*/
                stopWatch.Reset();
                stopWatch.Start();
                tmrDraw?.Change(Timeout.Infinite, Timeout.Infinite); //disable timer while we do our stuff


                /*-----------------------------*/
                /*  Update particle positions  */
                /*-----------------------------*/
                //UpdateDebug();
                //UpdateParticles();
                UpdateSand();


                /*---------------------------*/
                /*  Write to console buffer  */
                /*---------------------------*/
                lock (conLock)
                {
                    Console.CursorLeft = 0;
                    Console.CursorTop = 0;
                    Console.ForegroundColor = conColor;
                    Console.Write(ScreenArray);
                    Console.CursorLeft = 0;
                    Console.CursorTop = 0;
                }
            }
            catch (Exception ex)
            {
                Console.CursorLeft = 0;
                Console.Write($"> Array({ScreenArray.Length}) x={debug_x},y={debug_y}: {ex.Message}");
            }
            finally
            {
                stopWatch.Stop();
                if (stopWatch.ElapsedMilliseconds > 0) { Console.Title = $"Milliseconds per frame: {stopWatch.ElapsedMilliseconds} or {1000 / stopWatch.ElapsedMilliseconds} FPS"; }

                //fEnd = (double)DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0;
                //double fps = FramesPerSecond(fEnd - fStart);
                //Console.Title = $"FPS: {fps:000}";

                tmrDraw?.Change(TimeSpan.FromMilliseconds(RefreshRate), TimeSpan.FromMilliseconds(RefreshRate));
            }
        }

        //========================================================================================================================
        private static void UpdateDebug()
        {

            if (parts.Count > 0)
            {
                //update speed and position
                foreach (Particle p in parts)
                {
                    //clear previous location
                    Draw(p.x_pos, p.y_pos, ' ');


                    //Update location
                    p.x_pos += p.x_vel;
                    p.y_pos += p.y_vel;


                    //check X bounds
                    if (p.x_pos >= ScreenWidth - 3)
                    {
                        p.x_pos = ScreenWidth - 3;
                    }
                    else if (p.x_pos < 0)
                    {
                        p.x_pos = 0;
                    }

                    //check Y bounds
                    if (p.y_pos >= ScreenHeight)
                    {
                        p.y_pos = ScreenHeight - 1;
                    }
                    else if (p.y_pos < 0)
                    {
                        p.y_pos = 0;
                    }

                    Draw(p.x_pos, p.y_pos, p.glyph);
                }
            }


        }

        //========================================================================================================================
        private static void UpdateSand()
        {
            int idx_count = 0;
            int x_prev = 0;
            int y_prev = 0;
            int idx_color = rnd.Next(100);
            int max_active = ScreenHeight * 2;
            char char_test = ' ';
            bool pattern = true;
            bool poked = false;


            if (parts.Count >= maxSand)
            {
                if (--startOver <= 0)
                {
                    parts.Clear(); //clear particle array
                    ClearFrame();  //clear drawing buffer
                    startOver = 500;
                }
            }
            else
            {
                if (rnd.Next(100) > 45) //if ((cycles % 2 == 0) && parts.Count < maxSand)
                {
                    lock (parts)
                    {
                        if (idx_color > 40)
                        {
                            //parts.Add(new Particle { x_pos = ScreenWidth / 2, y_pos = 0, x_vel = 0, y_vel = 1, active = sandLife, settle = false, glyph = (char)PIXEL_TYPE.QUARTER });
                            parts.Add(new Particle { x_pos = rnd.Next((ScreenWidth / 2) - 1, (ScreenWidth / 2) + 1), y_pos = 0, x_vel = 0, y_vel = 1, active = sandLife, settle = false, glyph = (char)PIXEL_TYPE.QUARTER });
                        }
                        else if (idx_color > 12)
                        {
                            //parts.Add(new Particle { x_pos = ScreenWidth / 2, y_pos = 0, x_vel = 0, y_vel = 1, active = sandLife, settle = false, glyph = (char)PIXEL_TYPE.HALF });
                            parts.Add(new Particle { x_pos = rnd.Next((ScreenWidth / 2) - 1, (ScreenWidth / 2) + 1), y_pos = 0, x_vel = 0, y_vel = 1, active = sandLife, settle = false, glyph = (char)PIXEL_TYPE.HALF });
                        }
                        else
                        {
                            //parts.Add(new Particle { x_pos = ScreenWidth / 2, y_pos = 0, x_vel = 0, y_vel = 1, active = sandLife, settle = false, glyph = (char)PIXEL_TYPE.THREEQUARTERS });
                            parts.Add(new Particle { x_pos = rnd.Next((ScreenWidth / 2) - 1, (ScreenWidth / 2) + 1), y_pos = 0, x_vel = 0, y_vel = 1, active = sandLife, settle = false, glyph = (char)PIXEL_TYPE.THREEQUARTERS });
                        }
                    }
                }
            }

            if (parts.Count > 0)
            {
                //update speed and position
                foreach (Particle p in parts)
                {
                    idx_count++;

                    //check X coord
                    if ((p.x_pos + p.x_vel) >= ScreenWidth)
                    {
                        p.x_vel *= -1;
                    }
                    else if ((p.x_pos + p.x_vel) <= 0)
                    {
                        p.x_vel *= -1;
                    }
                    else
                    {
                        p.x_vel *= 1;
                    }

                    //check Y coord
                    if ((p.y_pos + p.y_vel) >= ScreenHeight)
                    {
                        p.y_vel = 0; //was "p.y_vel *= -1;"
                    }
                    else if ((p.y_pos + p.y_vel) <= 0)
                    {
                        p.y_vel *= -1;
                    }
                    else
                    {
                        p.y_vel *= 1;
                    }

                    //clear previous location
                    Draw(p.x_pos, p.y_pos, ' ');


                    //check where we're going to be
                    char_test = CheckLocation(p.x_pos + p.x_vel, p.y_pos + p.y_vel);
                    if (!char_test.Equals(' '))
                    {
                        if (p.active > 0)
                        {
                            p.active -= 1;

                            if (!p.settle)
                            {
                                if (rnd.Next(100) > 49)
                                {
                                    idx_color = rnd.Next(100);
                                    if (idx_color > 40)
                                    {
                                        p.x_vel = 2;
                                        p.y_vel = 1;
                                    }
                                    else
                                    {
                                        p.x_vel = 3;
                                        p.y_vel = 2;
                                    }
                                }
                                else
                                {
                                    idx_color = rnd.Next(100);
                                    if (idx_color > 40)
                                    {
                                        p.x_vel = -2;
                                        p.y_vel = 1;
                                    }
                                    else
                                    {
                                        p.x_vel = -3;
                                        p.y_vel = 2;
                                    }
                                }
                            }
                            else
                            {
                                //horizontal settle motion
                                idx_color = rnd.Next(100);
                                if (idx_color > 49)
                                {
                                    if (rnd.Next(100) > 39)
                                    {
                                        p.x_vel = -3;
                                        p.y_vel = 1;
                                    }
                                    else
                                    {
                                        p.x_vel = -4;
                                        p.y_vel = 2;
                                    }
                                }
                                else
                                {
                                    if (rnd.Next(100) > 39)
                                    {
                                        p.x_vel = 3;
                                        p.y_vel = 1;
                                    }
                                    else
                                    {
                                        p.x_vel = 4;
                                        p.y_vel = 2;
                                    }
                                }
                            }

                        }
                        else
                        {
                            p.active = 0; //we might be less than zero?
                            p.x_vel = 0;
                            p.y_vel = 0;
                        }
                    }
                    else
                    {
                        if (p.active > 0 && p.y_vel != 0)
                        {
                            p.active -= 1;
                            //Update location
                            p.x_pos += p.x_vel;
                            p.y_pos += p.y_vel;
                        }
                        else if (p.y_vel == 0)
                        {
                            p.active = 0; //signal that we can evaluate this piece later
                        }
                    }

                    //check X bounds
                    if (p.x_pos >= ScreenWidth - 1)
                    {
                        p.x_pos = ScreenWidth - 1;
                    }
                    else if (p.x_pos < 0)
                    {
                        p.x_pos = 0;
                    }

                    //check Y bounds
                    if (p.y_pos >= ScreenHeight - 1)
                    {
                        p.y_pos = ScreenHeight - 1;
                    }
                    else if (p.y_pos < 0)
                    {
                        p.y_pos = 0;
                    }

                    //Write to our screen buffer
                    if (pattern)
                    {
                        Draw(p.x_pos, p.y_pos, p.glyph);
                    }
                    else
                    {
                        if (p.active > 0 && !p.settle) //it can be jarring to change the color on the pieces that are settling, so check for p.settle flag
                        {
                            if ((idx_count == 1) & (p.y_vel == 0)) //first pixel is special
                                Draw(p.x_pos, p.y_pos, (char)PIXEL_TYPE.HALF);
                            else
                                Draw(p.x_pos, p.y_pos, (char)PIXEL_TYPE.QUARTER);
                        }
                        else if (p.settle) //it can be jarring to change the color on the pieces that are settling, so check for p.settle flag
                        {
                            Draw(p.x_pos, p.y_pos, (char)PIXEL_TYPE.HALF); //change this to see the settle mass stand out
                        }
                        else
                        {
                            Draw(p.x_pos, p.y_pos, (char)PIXEL_TYPE.HALF);
                        }
                    }

                    //Save last position
                    x_prev = p.x_pos;
                    y_prev = p.y_pos;
                }
            }


            //settle the pile every now and then
            if ((cycles % 200 == 0) && parts.Count > 0)
            {
                foreach (Particle p in parts)
                {
                    try
                    {
                        poked = false;

                        if (((p.y_pos + 1) <= ScreenHeight) && ((p.x_pos + 1) <= ScreenWidth) && ((p.y_pos - 1) >= 0) && ((p.x_pos - 1) >= 0)) //bounds check
                        {
                            //o-------------------o
                            //|  settle the pile  |
                            //o-------------------o
                            if (!poked)
                            {
                                char_test = CheckLocation(p.x_pos, p.y_pos + 1);
                                if (!char_test.Equals(' '))
                                {
                                    if (p.active <= 0) //only check inactive pieces
                                    {
                                        p.active = max_active;  //give x chances to get there
                                        p.settle = true;
                                        //p.x_vel = 0;
                                        p.y_vel = 1;
                                        poked = true;
                                    }
                                    else
                                    {
                                        p.active = max_active;  //give x chances to get there
                                        p.settle = true;
                                        //p.x_vel = 0;
                                        p.y_vel = 1;
                                        poked = true;
                                    }
                                }
                            }

                            if (!poked)
                            {
                                char_test = CheckLocation(p.x_pos - 1, p.y_pos);
                                if (!char_test.Equals(' '))
                                {
                                    if (p.active <= 0) //only check inactive pieces
                                    {
                                        p.active = max_active;  //give x chances to get there
                                        p.settle = true;
                                        p.x_vel = -1;
                                        p.y_vel = 1;
                                        poked = true;
                                    }
                                    else
                                    {
                                        p.active = max_active;  //give x chances to get there
                                        p.settle = true;
                                        p.x_vel = -1;
                                        p.y_vel = 1;
                                        poked = true;
                                    }
                                }
                            }

                            if (!poked)
                            {
                                char_test = CheckLocation(p.x_pos + 1, p.y_pos);
                                if (!char_test.Equals(' '))
                                {
                                    if (p.active <= 0) //only check inactive pieces
                                    {
                                        p.active = max_active;  //give x chances to get there
                                        p.settle = true;
                                        p.x_vel = 1;
                                        p.y_vel = 1;
                                        poked = true;
                                    }
                                    else
                                    {
                                        p.active = max_active;  //give x chances to get there
                                        p.settle = true;
                                        p.x_vel = 1;
                                        p.y_vel = 1;
                                        poked = true;
                                    }
                                }
                            }

                            //o-----------------------------------o
                            //|  Did anyone get past the checks?  |
                            //o-----------------------------------o
                            if (!poked && p.y_vel == 0)
                            {
                                p.active = max_active;  //give x chances to get there
                                p.settle = true;
                                poked = true;
                                //p.glyph = (char)PIXEL_TYPE.HOLLOW_DOT; //flag pieces that are unique
                            }

                        }
                        else
                        {
                            //p.glyph = (char)PIXEL_TYPE.HOLLOW_DOT; //flag pieces that are unique
                        }
                    }
                    catch
                    {
                        Console.Write($"> Out of bounds! ({p.x_pos},{p.y_pos})");
                    }
                }

            }

        }

        //========================================================================================================================
        private static void UpdateParticles()
        {
            int x_prev = -1;
            int y_prev = -1;
            int idx_count = 0;

            if (parts.Count > 0)
            {
                foreach (Particle p in parts)
                {
                    idx_count++;

                    //check X coord
                    if ((p.x_pos + p.x_vel) >= ScreenWidth)
                    {
                        p.x_vel *= -1;
                    }
                    else if ((p.x_pos + p.x_vel) <= 0)
                    {
                        p.x_vel *= -1;
                    }
                    else
                    {
                        //conColor = ConsoleColor.White;
                        p.x_vel *= 1;
                    }

                    //check Y coord
                    if ((p.y_pos + p.y_vel) >= ScreenHeight)
                    {
                        p.y_vel *= -1;
                    }
                    else if ((p.y_pos + p.y_vel) <= 0)
                    {
                        p.y_vel *= -1;
                    }
                    else
                    {
                        //conColor = ConsoleColor.White;
                        p.y_vel *= 1;
                    }

                    if (IsPositive(p.x_vel))
                    {
                        //Draw(p.x_pos, p.y_pos, (char)PIXEL_TYPE.QUARTER); //leave trace at previous location
                        Draw(p.x_pos, p.y_pos, ' '); //clear previous location
                    }
                    else
                    {
                        Draw(p.x_pos, p.y_pos, ' '); //clear previous location
                    }


                    //Update location
                    p.x_pos += p.x_vel;
                    p.y_pos += p.y_vel;

                    //Check for collision
                    /*
                    if (p.y_pos == y_prev)
                    {
                        p.y_vel *= -1;
                        conColor = ConsoleColor.Yellow;
                    }
                    if (p.x_pos == x_prev)
                    {
                        p.x_vel *= -1;
                        conColor = ConsoleColor.Red;
                    }
                    */

                    //Save last position
                    x_prev = p.x_pos;
                    y_prev = p.y_pos;

                    Draw(p.x_pos, p.y_pos, (char)PIXEL_TYPE.HALF); //set new location
                }
            }
        }

        public static bool IsPositive(int number)
        {
            return number > 0;
        }

        public static bool IsNegative(int number)
        {
            return number < 0;
        }
        private static void DrawTest(object state)
        {
            try
            {
                tmrTest?.Change(Timeout.Infinite, Timeout.Infinite); //disable timer while we do our stuff
                GenerateNoise();
            }
            catch (Exception ex)
            {
                Console.Write($"> ERROR: {ex.Message}");
            }
            finally
            {
                tmrTest?.Change(TimeSpan.FromMilliseconds(UpdateRate), TimeSpan.FromMilliseconds(UpdateRate));
            }
        }
        private static void GenerateNoise()
        {
            int rndY = rnd.Next(ScreenHeight);
            int rndX = rnd.Next(ScreenWidth);
            int path = rnd.Next(100);
            char test = ' ';
            ClearFrame();
            for (int x = 0; x < ScreenWidth; x++)
            {
                for (int y = 0; y < ScreenHeight; y++)
                {
                    rndX = rnd.Next(ScreenWidth);
                    rndY = rnd.Next(ScreenHeight);
                    path = rnd.Next(100);

                    test = CheckLocation(rndX, rndY);
                    if (!Char.IsWhiteSpace(test)) //roll dice again
                    {
                        rndX = rnd.Next(ScreenWidth);
                        rndY = rnd.Next(ScreenHeight);
                        test = CheckLocation(rndX, rndY);
                        if (!Char.IsWhiteSpace(test)) //roll dice again
                        {
                            rndX = rnd.Next(ScreenWidth);
                            rndY = rnd.Next(ScreenHeight);
                        }
                    }

                    if (path >= 88)
                    {
                        Draw(rndX, rndY, (char)PIXEL_TYPE.SOLID);
                    }
                    else if (path >= 60)
                    {
                        Draw(rndX, rndY, (char)PIXEL_TYPE.HALF);
                    }
                    else if (path >= 35)
                    {
                        Draw(rndX, rndY, (char)PIXEL_TYPE.THREEQUARTERS);
                    }
                    else
                    {
                        Draw(rndX, rndY, (char)PIXEL_TYPE.QUARTER);
                    }
                }
            }
        }

    }

    class Tetris : ConsoleGame
    {
        readonly Random rand = new Random();
        readonly string[] tetromino = new string[7];

        int[] levels;

        int[] playingField;

        static int fieldWidth = 24; static int fieldHeight = 44;

        int lineCount = 0;
        int frame = 0;
        int currentTetromino = 0;
        int rotation = 0;
        Point current;

        List<int> lines = new List<int>();

        int highscore = 0;
        int score = 0;
        int level = 0;

        bool gameover = false;

        private static void Main(string[] args)
        {
            new Tetris().Construct(fieldWidth + 2, fieldHeight + 6, 16, 16, FramerateMode.MaxFps);
        }
        public override void Create()
        {
            Engine.SetPalette(Palettes.Pico8);
            Engine.Borderless();
            Console.Title = "Tetris";
            TargetFramerate = 50;

            tetromino[0] = "..0...0...0...0.";
            tetromino[1] = "..1..11...1.....";
            tetromino[2] = ".....22..22.....";
            tetromino[3] = "..3..33..3......";
            tetromino[4] = ".4...44...4.....";
            tetromino[5] = ".5...5...55.....";
            tetromino[6] = "..6...6..66.....";

            levels = new int[30] { 48, 43, 38, 33, 28, 23, 18, 13, 8, 6, 5, 5, 5, 4, 4, 4, 3, 3, 3,
                2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1};

            Restart();
        }

        public override void Update()
        {
            if (!gameover)
            {
                // input
                if (Engine.GetKeyDown(ConsoleKey.RightArrow))
                {
                    current.X += DoesPieceFit(currentTetromino, rotation, current + new Point(1, 0)) ? 1 : 0;
                }
                if (Engine.GetKeyDown(ConsoleKey.LeftArrow))
                {
                    current.X -= DoesPieceFit(currentTetromino, rotation, current - new Point(1, 0)) ? 1 : 0;
                }
                if (Engine.GetKey(ConsoleKey.DownArrow) && frame % 3 == 0)
                {
                    current.Y += DoesPieceFit(currentTetromino, rotation, current + new Point(0, 1)) ? 1 : 0;
                }
                if (Engine.GetKeyDown(ConsoleKey.UpArrow))
                {
                    rotation += DoesPieceFit(currentTetromino, rotation + 1, current) ? 1 : 0;
                }
                if (Engine.GetKeyDown(ConsoleKey.Escape))
                {
                    Environment.Exit(1);
                }


                frame++;

                // updating framerate
                if (frame % levels[level] == 0)
                {
                    if (DoesPieceFit(currentTetromino, rotation, current + new Point(0, 1)))
                    {
                        current.Y += 1;
                    }
                    else
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            for (int py = 0; py < 4; py++)
                            {
                                if (tetromino[currentTetromino][Rotate(new Point(px, py), rotation)] != '.')
                                {
                                    playingField[(current.Y + py) * fieldWidth + (current.X + px)] = currentTetromino + 1;
                                }
                            }
                        }

                        for (int py = 0; py < 4; py++)
                        {
                            if (current.Y + py < fieldHeight - 1)
                            {
                                bool bline = true;
                                for (int px = 1; px < fieldWidth - 1; px++) bline &= (playingField[(current.Y + py) * fieldWidth + px]) != 0;

                                if (bline)
                                {
                                    for (int px = 1; px < fieldWidth - 1; px++)
                                    {
                                        playingField[(current.Y + py) * fieldWidth + px] = 8;
                                    }

                                    lines.Add(current.Y + py);
                                }
                            }
                        }

                        // gravity
                        if (lines.Any())
                        {
                            for (int line = 0; line < lines.Count; line++)
                            {
                                for (int x = 1; x < fieldWidth - 1; x++)
                                {
                                    for (int y = lines[line]; y > 0; y--)
                                    {
                                        if (y - 1 > 0) playingField[y * fieldWidth + x] = playingField[(y - 1) * fieldWidth + x];
                                        else playingField[y * fieldWidth + x] = 0;
                                    }
                                }
                            }

                            lineCount += lines.Count;
                            switch (lines.Count)
                            {
                                case 1: score += 40 * (level + 1); break;
                                case 2: score += 100 * (level + 1); break;
                                case 3: score += 300 * (level + 1); break;
                                case 4: score += 1200 * (level + 1); break;
                            }
                        }
                        lines.Clear();

                        current.X = fieldWidth / 2 - 2;
                        current.Y = 0;
                        rotation = 0;
                        currentTetromino = rand.Next(0, tetromino.Length);

                        if (lineCount > 10)
                        {
                            lineCount = 0;
                            level++;
                        }

                        gameover = !DoesPieceFit(currentTetromino, rotation, current);
                    }
                }

            }
            else
            {
                Engine.Fill(new Point(fieldWidth / 2 - 5, fieldHeight / 2 - 1), new Point(fieldWidth / 2 + 6, fieldHeight / 2 + 3), 0, ConsoleCharacter.Full);
                Engine.WriteText(new Point(fieldWidth / 2 - 4, fieldHeight / 2), "Game Over!", 8);
                Engine.WriteText(new Point(fieldWidth / 2 - 4, fieldHeight / 2 + 1), "Highscore:", 7);

                if (highscore < score)
                {
                    highscore = score;
                }

                Engine.WriteText(new Point(fieldWidth / 2 - 4, fieldHeight / 2 + 2), highscore.ToString(), 9);

                Engine.Frame(new Point(fieldWidth / 2 - 5, fieldHeight / 2 - 1), new Point(fieldWidth / 2 + 6, fieldHeight / 2 + 3), 7);

                Engine.DisplayBuffer();

                Thread.Sleep(4000);
                Restart();
            }
        }

        public override void Render()
        {
            Engine.ClearBuffer();

            for (int x = 0; x < fieldWidth; x++)
            {
                for (int y = 0; y < fieldHeight; y++)
                {
                    if (playingField[(y) * fieldWidth + x] != 0) Engine.SetPixel(new Point(x + 1, y + 1), playingField[y * fieldWidth + x] + 7);
                }
            }

            for (int px = 0; px < 4; px++)
            {
                for (int py = 0; py < 4; py++)
                {
                    if (tetromino[currentTetromino][Rotate(new Point(px, py), rotation)] != '.')
                    {
                        Engine.SetPixel(new Point(current.X + px + 1, current.Y + py + 1), GetTetrominoColor(currentTetromino) + 8);
                    }
                }
            }
            Engine.Frame(new Point(1, 0), new Point(fieldWidth, fieldHeight), 7);
            Engine.WriteText(new Point(2, fieldHeight + 1), "Score", 7);
            Engine.WriteText(new Point(fieldWidth - score.ToString("N0").Count(), fieldHeight + 1), score.ToString("N0"), 9);
            Engine.WriteText(new Point(2, fieldHeight + 2), "Line", 7);
            Engine.WriteText(new Point(fieldWidth - score.ToString().Count(), fieldHeight + 2), lineCount.ToString(), 7);
            Engine.WriteText(new Point(2, fieldHeight + 3), "Level", 7);
            Engine.WriteText(new Point(fieldWidth - score.ToString().Count(), fieldHeight + 3), level.ToString(), 7);

            Engine.DisplayBuffer();
        }



        int Rotate(Point p, int r)
        {
            int i = 0;
            switch (r % 4)
            {
                case 0:     // 0
                    i = p.Y * 4 + p.X;
                    break;
                case 1:     // 90
                    i = 12 + p.Y - (p.X * 4);
                    break;
                case 2:     // 180
                    i = 15 - (p.Y * 4) - p.X;
                    break;
                case 3:     // 270
                    i = 3 - p.Y + (p.X * 4);
                    break;
            }
            return i;
        }

        bool DoesPieceFit(int selTetromino, int rot, Point pos)
        {
            for (int px = 0; px < 4; px++)
            {
                for (int py = 0; py < 4; py++)
                {
                    int pieceIndex = Rotate(new Point(px, py), rot);

                    int fieldIndex = (pos.Y + py) * fieldWidth + (pos.X + px);

                    if (pos.X + px >= 0 && pos.X + px < fieldWidth &&
                        pos.Y + py >= 0 && pos.Y + py < fieldHeight)
                    {
                        if (tetromino[selTetromino][pieceIndex] != '.' && playingField[fieldIndex] != 0) return false;
                    }
                }
            }

            return true;
        }

        int GetTetrominoColor(int t)
        {
            Match m = Regex.Match(tetromino[t], @"\d");
            return Convert.ToInt32(m.Value[0]);
        }

        void Restart()
        {
            score = 0;
            gameover = false;

            playingField = new int[fieldWidth * fieldHeight];
            for (int x = 0; x < fieldWidth; x++)
                for (int y = 0; y < fieldHeight; y++)
                    playingField[y * fieldWidth + x] = (x == 0 || x == fieldWidth - 1 || y == fieldHeight - 1) ? -1 : 0;    // väggar

            current = new Point(fieldWidth / 2 - 2, 0);
            currentTetromino = rand.Next(0, tetromino.Length);
        }
    }

    //=========================================================================================================================
    //=========================================================================================================================
    //=========================================================================================================================
    internal struct Block
    {
        internal int x;
        internal int y;
        internal bool alive;
        internal int color;

        internal Block(int xpos, int ypos, bool a, int c)
        {
            x = xpos;
            y = ypos;
            alive = a;
            color = c;
        }
    }
    internal class Breakout : ConsoleGame
    {
        static int fieldWidth = 34; //24
        static int fieldHeight = 30; //30
        static int ballDirectionUpRight = 0;
        static int ballDirectionUpLeft = 1;
        static int ballDirectionDownRight = 2;
        static int ballDirectionDownLeft = 3;
        static int blockRows = 8; //8
        static int blockCols = 9; //6
        static int gameStateStart = 0;
        static int gameStatePlaying = 1;
        static int gameStateEnd = 2;
        static int startingBlockY = 2;

        static int startingX = 8;
        static int startingY = 23;
        static int startingBallX = 12;
        static int startingBallY = 22;
        static int defaultBallWidth = 1;
        static int defaultBallHeight = 1;
        static int startingBallXVel = 1;
        static int startingBallYVel = 1;
        static int startingBallMoveDelay = 4;
        static int startingBallDirection = ballDirectionUpRight;
        static int defaultPaddleWidth = 5; //4
        static int defaultPaddleHeight = 1;
        static int startingTurns = 3;
        static int startingGameState = gameStateStart;

        int paddlex;
        int paddley;
        int width;
        int height;

        int ballx;
        int bally;
        int ballVelx;
        int ballVely;
        int ballWidth;
        int ballHeight;
        int ballDirection;
        int ballMoveCounter;
        int ballMoveDelay;
        Block[,] blocks;
        int score;
        int turns;
        int gameState;
        string gameOverText;

        private static void Main(string[] args)
        {
            new Breakout().Construct(fieldWidth, fieldHeight, 16, 16, FramerateMode.MaxFps);
        }

        public override void Create()
        {
            Engine.SetPalette(Palettes.Pico8);
            Engine.Borderless();
            Console.Title = "Breakout";
            TargetFramerate = 60;
            blocks = new Block[blockRows, blockCols];
            width = defaultPaddleWidth;
            height = defaultPaddleHeight;
            gameOverText = "";
            int color = 8;
            for (int y = 0; y < blockRows; y++)
            {
                for (int x = 0; x < blockCols; x++)
                {
                    blocks[y, x] = new Block(x * width, (y * height) + startingBlockY, false, color);
                }
                color++;
            }
            gameState = startingGameState;
        }

        public override void Render()
        {
            Engine.ClearBuffer();

            if (gameState == gameStateStart)
            {
                Engine.WriteText(new Point(1, 4), "Breakout", 7);
                Engine.WriteText(new Point(1, 8), "Press Enter to Play", 7);
                Engine.WriteText(new Point(1, 12), "Press Del to Exit", 7);


            }
            else if (gameState == gameStatePlaying)
            {
                Engine.Fill(new Point(paddlex, paddley), new Point(paddlex + width, paddley + height), 7);

                Engine.Fill(new Point(ballx, bally), new Point(ballx + ballWidth, bally + ballHeight), 7);

                for (int y = 0; y < blockRows; y++)
                {
                    for (int x = 0; x < blockCols; x++)
                    {
                        if (blocks[y, x].alive)
                        {
                            Engine.Fill(new Point(blocks[y, x].x, blocks[y, x].y), new Point(blocks[y, x].x + width, blocks[y, x].y + height), blocks[y, x].color);
                        }
                    }
                }

                Engine.WriteText(new Point(1, fieldHeight - 4), "Score: " + score, 7);
                Engine.WriteText(new Point(1, fieldHeight - 2), "Turns: " + turns, 7);
            }
            else if (gameState == gameStateEnd)
            {
                Engine.WriteText(new Point(1, 4), gameOverText, 7);
                Engine.WriteText(new Point(1, 8), "Final Score: " + score, 7);
                Engine.WriteText(new Point(1, 12), "Press Enter to Play", 7);
                Engine.WriteText(new Point(1, 16), "Press del to Exit", 7);
            }
            Engine.DisplayBuffer();
        }

        public override void Update()
        {
            if (gameState == gameStateStart)
            {
                if (Engine.GetKeyDown(ConsoleKey.Enter))
                {
                    reset();
                    gameState = gameStatePlaying;
                }
            }
            if (gameState == gameStatePlaying)
            {
                // hanterar input
                if (Engine.GetKeyDown(ConsoleKey.RightArrow))
                {
                    paddlex++;
                    if (paddlex > (fieldWidth - width))
                    {
                        paddlex = fieldWidth - width;
                    }

                }
                if (Engine.GetKeyDown(ConsoleKey.LeftArrow))
                {
                    paddlex--;
                    if (paddlex < 0) paddlex = 0;
                }

                // Update ball
                ballMoveCounter++;
                if (ballMoveCounter > ballMoveDelay)
                {
                    if (ballDirection == ballDirectionUpLeft)
                    {
                        bally -= ballVely;
                        ballx -= ballVelx;
                    }
                    else if (ballDirection == ballDirectionUpRight)
                    {
                        bally -= ballVely;
                        ballx += ballVelx;
                    }
                    else if (ballDirection == ballDirectionDownLeft)
                    {
                        bally += ballVely;
                        ballx -= ballVelx;
                    }
                    else if (ballDirection == ballDirectionDownRight)
                    {
                        bally += ballVely;
                        ballx += ballVelx;
                    }

                    // Collide top
                    if (bally < 0)
                    {
                        bally = 0;
                        if (ballDirection == ballDirectionUpLeft)
                            ballDirection = ballDirectionDownLeft;
                        else if (ballDirection == ballDirectionUpRight)
                            ballDirection = ballDirectionDownRight;
                    }

                    if (collidePaddle())
                    {
                        bally -= ballVely;
                        if (ballDirection == ballDirectionDownLeft)
                            ballDirection = ballDirectionUpLeft;
                        else if (ballDirection == ballDirectionDownRight)
                            ballDirection = ballDirectionUpRight;
                    }

                    if (collideBlocks())
                    {
                        if (ballDirection == ballDirectionDownLeft)
                        {
                            ballDirection = ballDirectionUpLeft;
                        }
                        else if (ballDirection == ballDirectionDownRight)
                        {
                            ballDirection = ballDirectionUpRight;
                        }
                        else if (ballDirection == ballDirectionUpLeft)
                        {
                            ballDirection = ballDirectionDownLeft;
                        }
                        else if (ballDirection == ballDirectionUpRight)
                        {
                            ballDirection = ballDirectionDownRight;
                        }
                    }

                    // collide left
                    if (ballx < 0)
                    {
                        ballx = 0;
                        if (ballDirection == ballDirectionDownLeft)
                        {
                            ballDirection = ballDirectionDownRight;
                        }
                        else if (ballDirection == ballDirectionDownRight)
                        {
                            ballDirection = ballDirectionDownLeft;
                        }
                        else if (ballDirection == ballDirectionUpLeft)
                        {
                            ballDirection = ballDirectionUpRight;
                        }
                        else if (ballDirection == ballDirectionUpRight)
                        {
                            ballDirection = ballDirectionUpLeft;
                        }
                    }

                    // collide right
                    if (ballx >= fieldWidth - ballWidth)
                    {
                        ballx = fieldWidth - ballWidth;
                        if (ballDirection == ballDirectionDownLeft)
                        {
                            ballDirection = ballDirectionDownRight;
                        }
                        else if (ballDirection == ballDirectionDownRight)
                        {
                            ballDirection = ballDirectionDownLeft;
                        }
                        else if (ballDirection == ballDirectionUpLeft)
                        {
                            ballDirection = ballDirectionUpRight;
                        }
                        else if (ballDirection == ballDirectionUpRight)
                        {
                            ballDirection = ballDirectionUpLeft;
                        }
                    }

                    // Collide down
                    if (bally > fieldHeight)
                    {
                        turns--;
                        if (turns <= 0)
                        {
                            gameState = gameStateEnd;
                            gameOverText = "Game Over";
                        }
                        bally = startingBallY;
                        ballDirection = startingBallDirection;
                    }

                    ballMoveCounter = 0;
                }
            }
            else if (gameState == gameStateEnd)
            {
                if (Engine.GetKeyDown(ConsoleKey.Enter))
                {
                    gameState = gameStateStart;
                }
            }

            if (Engine.GetKeyDown(ConsoleKey.Escape))
            {
                Environment.Exit(1);
            }

        }

        private bool collidePaddle()
        {
            bool result = false;

            if (bally >= paddley && ballx >= paddlex && ballx < (paddlex + width) && bally <= (paddley + height))
            {
                result = true;
            }

            return result;
        }
        private bool collideBlocks()
        {
            bool result = false;
            for (int y = 0; y < blockRows; y++)
            {
                for (int x = 0; x < blockCols; x++)
                {
                    if (blocks[y, x].alive)
                    {
                        if (bally >= blocks[y, x].y && bally <= (blocks[y, x].y + height) && ballx >= blocks[y, x].x && ballx < (blocks[y, x].x + width))
                        {
                            blocks[y, x].alive = false;
                            score += 10;
                            result = true;
                            if (allBlocksDead())
                            {
                                gameState = gameStateEnd;
                                gameOverText = "You Win!";
                            }
                            break;
                        }
                    }
                }
                if (result)
                {
                    break;
                }
            }
            return result;
        }

        private bool allBlocksDead()
        {
            bool result = true;
            for (int y = 0; y < blockRows; y++)
            {
                for (int x = 0; x < blockCols; x++)
                {
                    if (blocks[y, x].alive)
                    {
                        result = false;
                        break;
                    }
                }
                if (!result)
                {
                    break;
                }
            }
            return result;
        }

        private void reset()
        {
            paddlex = startingX;
            paddley = startingY;
            ballx = startingBallX;
            bally = startingBallY;
            ballWidth = defaultBallWidth;
            ballHeight = defaultBallHeight;
            ballVelx = startingBallXVel;
            ballVely = startingBallYVel;
            ballMoveCounter = 0;
            ballMoveDelay = startingBallMoveDelay;
            ballDirection = startingBallDirection;
            gameState = startingGameState;
            score = 0;
            turns = startingTurns;

            for (int y = 0; y < blockRows; y++)
            {
                for (int x = 0; x < blockCols; x++)
                {
                    blocks[y, x].alive = true;
                }
            }
        }
    }

    //=========================================================================================================================
    //=========================================================================================================================
    //=========================================================================================================================
    class Sokoban : ConsoleGame
    {
        private static void Main(string[] args)
        {
            new Sokoban().Construct(16, 16, 16, 16, FramerateMode.Unlimited);
        }

        int[,] map;
        Point player = new Point(1, 2);

        public override void Create()
        {
            Engine.SetPalette(Palettes.Pico8);
            Engine.Borderless();

            map = new int[10, 8] {
                { 0, 0, 0, 1, 1, 1, 1, 1},
                { 1, 1, 1, 1, 0, 0, 0, 1},
                { 1, 0, 3, 0, 0, 1, 0, 1},
                { 1, 0, 3, 3, 3, 0, 0, 1},
                { 1, 1, 4, 1, 4, 0, 0, 1},
                { 1, 0, 0, 4, 4, 1, 0, 1},
                { 1, 0, 0, 1, 4, 3, 0, 1},
                { 1, 0, 3, 4, 3, 0, 1, 1},
                { 1, 1, 1, 0, 0, 0, 1, 0},
                { 0, 0, 1, 1, 1, 1, 1, 0},
            };



        }

        public override void Update()
        {
            if (Engine.GetKeyDown(ConsoleKey.UpArrow))
            {

            }
        }

        public override void Render()
        {
            Engine.ClearBuffer();

            Engine.Frame(new Point(1, 1), new Point(14, 14), 7);

            Point offset = new Point(4, 3);

            for (int y = 0; y < map.GetLength(0); y++)
            {
                for (int x = 0; x < map.GetLength(1); x++)
                {
                    Point p = new Point(x, y) + offset;

                    switch (map[y, x])
                    {
                        case 1:
                            Engine.SetPixel(p, 7, ConsoleCharacter.Full);
                            break;
                        case 3:
                            Engine.SetPixel(p, 9, ConsoleCharacter.Full);
                            break;
                        case 4:
                            Engine.SetPixel(p, 8, (ConsoleCharacter)'x');
                            break;
                    }
                }
            }

            Engine.SetPixel(player + offset, 8, (ConsoleCharacter)'@');

            Engine.DisplayBuffer();
        }
    }


    //=========================================================================================================================
    //=========================================================================================================================
    //=========================================================================================================================
    class CaveGenerator : ConsoleGame
    {
        static void Main(string[] args)
        {
            new CaveGenerator().Construct(size.X, size.Y + 1, 8, 8, FramerateMode.MaxFps);
        }

        static Point size = new Point(106, 49); //new Point(96, 64)

        int[,] m;
        Random rand = new Random();

        private int seed;
        private int rfp = 48;
        private int scount = 6;
        private int max = 4;
        private int min = 4;

        int sel = 0;

        public override void Create()
        {
            Engine.SetPalette(Palettes.Default);
            Engine.Borderless();

            seed = rand.Next(int.MinValue, int.MaxValue);
            m = Generate(size.X, size.Y, rfp, scount, max, min, seed);
        }

        public override void Update()
        {
            if (Engine.GetKeyDown(ConsoleKey.Spacebar))
            {
                seed = rand.Next(int.MinValue, int.MaxValue);
                m = Generate(size.X, size.Y, rfp, scount, max, min, seed);
            }


            if (Engine.GetKeyDown(ConsoleKey.RightArrow) && sel != 3) sel++;
            if (Engine.GetKeyDown(ConsoleKey.LeftArrow) && sel != 0) sel--;

            if (Engine.GetKeyDown(ConsoleKey.UpArrow)) switch (sel)
                {
                    case 0: rfp++; break;
                    case 1: scount++; break;
                    case 2: max++; break;
                    case 3: min++; break;
                }
            if (Engine.GetKeyDown(ConsoleKey.DownArrow)) switch (sel)
                {
                    case 0: rfp--; break;
                    case 1: scount--; break;
                    case 2: max--; break;
                    case 3: min--; break;
                }
        }

        public override void Render()
        {
            Engine.ClearBuffer();

            for (int i = 0; i < size.X; i++)
            {
                for (int j = 0; j < size.Y; j++)
                {
                    int col = (m[i, j] == 1) ? 0 : 15;
                    Engine.SetPixel(new Point(i, j), col);
                }
            }

            Engine.WriteText(new Point(0, size.Y), $"S: {seed}", 8);
            Engine.WriteText(new Point(15, size.Y), $"W: {size.ToString()}", 8);
            Engine.WriteText(new Point(32, size.Y), $"RFP: {rfp}", 8);
            Engine.WriteText(new Point(41, size.Y), $"S: {scount}", 8);
            Engine.WriteText(new Point(48, size.Y), $"MX: {max}", 8);
            Engine.WriteText(new Point(55, size.Y), $"MN: {min}", 8);

            switch (sel)
            {
                case 0: Engine.WriteText(new Point(32, size.Y), $"RFP: {rfp}", 12); break;
                case 1: Engine.WriteText(new Point(41, size.Y), $"S: {scount}", 12); break;
                case 2: Engine.WriteText(new Point(48, size.Y), $"MX: {max}", 12); break;
                case 3: Engine.WriteText(new Point(55, size.Y), $"MN: {min}", 12); break;

            }

            Engine.DisplayBuffer();
        }


        public int[,] Generate(int width, int height, int randomFillPercent = 45, int smoothCount = 5, int maxNeighbors = 4, int minNeighbors = 4, int seed = -1)
        {

            int[,] map = new int[width, height];

            if (seed == -1) seed = rand.Next(int.MinValue, int.MaxValue);
            Random prng = new Random(seed);

            // Generera
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (x == 0 || x == width - 1 || y == 0 || y == height - 1)
                    {
                        map[x, y] = 1;
                    }
                    else
                    {
                        map[x, y] = (prng.Next(0, 100) < randomFillPercent) ? 1 : 0;
                    }
                }
            }

            // Smooth
            int[,] smoothMap = new int[width, height];
            for (int i = 0; i < smoothCount; i++)
            {

                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        int neighbors = CountNeighbors(map, x, y, width, height);

                        if (neighbors > maxNeighbors) smoothMap[x, y] = 1;
                        else if (neighbors < minNeighbors) smoothMap[x, y] = 0;
                    }
                }
                map = smoothMap;
            }

            return map;
        }

        public int CountNeighbors(int[,] map, int gridX, int gridY, int w, int h)
        {
            int count = (map[gridX, gridY] == 1) ? -1 : 0;      // exkludera center ifall den är en vägg
            for (int x = gridX - 1; x <= gridX + 1; x++)
            {
                for (int y = gridY - 1; y <= gridY + 1; y++)
                {
                    if (x < 0 || x >= w || y < 0 || y >= h) { count++; continue; }
                    count += map[x, y];
                }
            }

            return count;
        }
    }
}
