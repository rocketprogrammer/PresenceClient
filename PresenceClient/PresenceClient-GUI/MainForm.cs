using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Timers;
using System.Windows.Forms;
using DiscordRPC;
using Newtonsoft.Json;
using PresenceClient_GUI.Properties;
using PresenceCommon.Types;
using Timer = System.Timers.Timer;
#if DEBUG
using DiscordRPC.Logging;
#endif

namespace PresenceClient_GUI;

public partial class MainForm : Form
{
    private Thread listenThread;
    private static Socket client;
    private static DiscordRpcClient rpc;
    private IPAddress ipAddress;
    private bool manualUpdate;
    private string lastProgramName = string.Empty;
    private Timestamps time;
    private static Timer timer;
    private bool hasSeenMacPrompt;

    public MainForm()
    {
        InitializeComponent();
        listenThread = new Thread(TryConnect);
    }

    private void ConnectButton_Click(object sender, EventArgs e)
    {
        if (connectButton.Text == "Connect")
        {
            // Check and see if ClientID is empty
            if (string.IsNullOrWhiteSpace(clientBox.Text))
            {
                Show();
                Activate();
                UpdateStatus("Client ID cannot be empty", Color.DarkRed);
                SystemSounds.Exclamation.Play();
                return;
            }

            // Check and see if we have an IP
            // If we have an IP, prompt to swap to MAC Address
            if (IPAddress.TryParse(addressBox.Text, out ipAddress))
            {
                if (!hasSeenMacPrompt)
                {
                    hasSeenMacPrompt = true;

                    var message = "We've detected that you're using an IP to connect to your device. Connecting via MAC address may make it easier to reconnect to your device in case the IP changes." +
                                  "\n\nWould you like to swap to connecting via MAC address? \n(We'll only ask this once.)";

                    if (MessageBox.Show(message, "IP Detected", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    {
                        UseMacDefault.Checked = true;
                        IpToMac();
                    }
                    else
                        UseMacDefault.Checked = false;
                }
                else if (UseMacDefault.Checked)
                    IpToMac();
            }
            else
            {
                // If in this block, means we dont have a valid IP.
                // Check and see if it's a MAC Address
                try
                {
                    IPAddress.TryParse(Utils.GetIpByMac(addressBox.Text), out ipAddress);
                }
                catch (FormatException)
                {
                    Show();
                    Activate();
                    UpdateStatus("Invalid IP or MAC Address", Color.DarkRed);
                    SystemSounds.Exclamation.Play();
                    return;
                }
            }

            listenThread.Start();

            connectButton.Text = "Disconnect";
            connectToolStripMenuItem.Text = "Disconnect";

            addressBox.Enabled = false;
            clientBox.Enabled = false;
        }
        else
        {
            listenThread.Abort();
            if (rpc != null && !rpc.IsDisposed)
            {
                rpc.ClearPresence();
                rpc.Dispose();
            }

            if (client != null) client.Close();
            if (timer != null) timer.Dispose();
            listenThread = new Thread(TryConnect);
            UpdateStatus("", Color.Gray);
            connectButton.Text = "Connect";
            connectToolStripMenuItem.Text = "Connect";
            trayIcon.Icon = Resources.Disconnected;
            trayIcon.Text = "PresenceClient (Disconnected)";

            ipAddress = null;
            addressBox.Enabled = true;
            clientBox.Enabled = true;
            lastProgramName = string.Empty;
            time = null;
        }
    }

    private void OnConnectTimeout(object source, ElapsedEventArgs e)
    {
        lastProgramName = string.Empty;
        time = null;
    }

    private void TryConnect()
    {
        if (rpc != null && !rpc.IsDisposed)
        {
            rpc.ClearPresence();
            rpc.Dispose();
        }

        rpc = new DiscordRpcClient(clientBox.Text);
        rpc.Initialize();

        //Create a timer that will be enabled when we lose connection to the server. 
        //Once the full time has passed, it will clear the info it had of the previous game
        timer = new Timer
        {
            Interval = 60000,
            SynchronizingObject = this,
            Enabled = false,
        };
        timer.Elapsed += OnConnectTimeout;

#if DEBUG
        rpc.Logger = new ConsoleLogger { Level = LogLevel.Warning };
        //Subscribe to events
        rpc.OnReady += (_, obj) =>
        {
            Console.WriteLine("Received Ready from user {0}", obj.User.Username);
        };

        rpc.OnPresenceUpdate += (_, obj) =>
        {
            Console.WriteLine("Received Update! {0}", obj.Presence);
        };
#endif

        while (true)
        {
            client = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveTimeout = 5500,
                SendTimeout = 5500,
            };

            UpdateStatus("Attemping to connect to server...", Color.Gray);
            trayIcon.Icon = Resources.Disconnected;
            trayIcon.Text = "PresenceClient (Connecting...)";
            timer.Enabled = true;

            try
            {
                var localEndPoint = new IPEndPoint(ipAddress, 0xCAFE);

                var result = client.BeginConnect(localEndPoint, null, null);
                var success = result.AsyncWaitHandle.WaitOne(2000, true);
                if (!success)
                {
                    //UpdateStatus("Could not connect to Server! Retrying...", Color.DarkRed);
                    client.Close();
                }
                else
                {
                    client.EndConnect(result);
                    timer.Enabled = false;

                    DataListen();
                }
            }
            catch (ArgumentNullException)
            {
                //The ip address is null because arp couldn't find the target mac address.
                //So we sleep and search for it again.
                Thread.Sleep(1000);
                IPAddress.TryParse(Utils.GetIpByMac(addressBox.Text), out ipAddress);
            }
            catch (SocketException)
            {
                client.Close();
                if (rpc != null && !rpc.IsDisposed) rpc.ClearPresence();
            }
        }
    }

    private void DataListen()
    {
        manualUpdate = true;
        while (true)
        {
            try
            {
                var bytes = PresenceCommon.Utils.ReceiveExactly(client);
                UpdateStatus("Connected to the server!", Color.Green);
                trayIcon.Icon = Resources.Connected;
                trayIcon.Text = "PresenceClient (Connected)";

                var title = new Title(bytes);
                if (title.Magic == 0xffaadd23)
                {
                    if (lastProgramName != title.Name)
                    {
                        time = Timestamps.Now;
                    }
                    if ((lastProgramName != title.Name) || manualUpdate)
                    {
                        if (rpc != null)
                        {

                            if (!checkMainMenu.Checked && title.Name == "Home Menu")
                                rpc.ClearPresence();
                            else
                            {
                                rpc.SetPresence(PresenceCommon.Utils.CreateDiscordPresence(title, time, bigKeyBox.Text, bigTextBox.Text, smallKeyBox.Text, stateBox.Text, checkTime.Checked));
                            }
                        }
                        manualUpdate = false;
                        lastProgramName = title.Name;
                    }
                }
                else
                {
                    if (rpc != null && !rpc.IsDisposed) rpc.ClearPresence();
                    client.Close();
                    return;
                }
            }
            catch (SocketException)
            {
                if (rpc != null && !rpc.IsDisposed) rpc.ClearPresence();
                client.Close();
                return;
            }
        }
    }

    private void IpToMac()
    {
        var macAddress = Utils.GetMacByIp(ipAddress.ToString());
        if (macAddress != null)
            addressBox.Text = macAddress;
        else
            MessageBox.Show("Can't convert to MAC Address! Sorry!");
    }

    private void MainForm_Load(object sender, EventArgs e)
    {
        if (File.Exists("Config.json"))
        {
            var cfg = JsonConvert.DeserializeObject<Config>(File.ReadAllText("Config.json"));
            checkTime.Checked = cfg.DisplayTimer;
            bigKeyBox.Text = cfg.BigKey;
            bigTextBox.Text = cfg.BigText;
            smallKeyBox.Text = cfg.SmallKey;
            addressBox.Text = cfg.Ip;
            stateBox.Text = cfg.State;
            clientBox.Text = cfg.Client;
            checkTray.Checked = cfg.AllowTray;
            checkMainMenu.Checked = cfg.DisplayMainMenu;
            hasSeenMacPrompt = cfg.SeenAutoMacPrompt;
            UseMacDefault.Checked = cfg.AutoToMac;
        }
    }

    private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
    {
        listenThread.Abort();
        if (rpc != null && !rpc.IsDisposed)
        {
            rpc.ClearPresence();
            rpc.Dispose();
        }

        if (client != null) client.Close();
    }

    private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && checkTray.Checked)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            if (timer != null) timer.Dispose();

            var cfg = new Config
            {
                Ip = addressBox.Text,
                Client = clientBox.Text,
                BigKey = bigKeyBox.Text,
                SmallKey = smallKeyBox.Text,
                State = stateBox.Text,
                BigText = bigTextBox.Text,
                DisplayTimer = checkTime.Checked,
                AllowTray = checkTray.Checked,
                DisplayMainMenu = checkMainMenu.Checked,
                SeenAutoMacPrompt = hasSeenMacPrompt,
                AutoToMac = UseMacDefault.Checked
            };
            File.WriteAllText("Config.json", JsonConvert.SerializeObject(cfg, Formatting.Indented));
        }
    }

    private void TrayIcon_MouseDoubleClick(object sender, MouseEventArgs e)
    {
        Show();
        Activate();
    }

    private void UpdateStatus(string text, Color color)
    {
        MethodInvoker inv = () =>
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = color;
        };
        Invoke(inv);
    }

    private void CheckTime_CheckedChanged(object sender, EventArgs e) => manualUpdate = true;

    private void BigKeyBox_TextChanged(object sender, EventArgs e) => manualUpdate = true;

    private void SKeyBox_TextChanged(object sender, EventArgs e) => manualUpdate = true;

    private void StateBox_TextChanged(object sender, EventArgs e) => manualUpdate = true;

    private void BigTextBox_TextChanged(object sender, EventArgs e) => manualUpdate = true;

    private void TrayExitMenuItem_Click(object sender, EventArgs e) => Application.Exit();

    private void LinkLabel1_LinkClicked_1(object sender, LinkLabelLinkClickedEventArgs e) => Process.Start($"https://discordapp.com/developers/applications/{clientBox.Text}");

    private void CheckMainMenu_CheckedChanged(object sender, EventArgs e) => manualUpdate = true;

    private void UseMacDefault_CheckedChanged(object sender, EventArgs e) => hasSeenMacPrompt = true;

    private void clientBox_TextChanged(object sender, EventArgs e)
    {

    }
}