using System;
using System.Threading;
using AsterNet.Standard;
using AsterNet.Standard.Models;

/// <summary>
/// It simulates an outbound progressive/predictive call scenario by
/// 
///    1) dialing the customer
///    2) performing call progress analysis
///    3) playing a message to the customer after the customer answers the call
///    4) creating a bridge
///    5) transferring the customer to the bridge
///    6) playing queue music for the customer via the bridge
///    7) dialing the agent in the background
///    8) transferring the agent to the bridge after the agent answers the call
///    9) stopping the queue music and letting the agent and the customer to talk to each other
///    10) recording the agent - customer conversation via the bridge
/// 
/// </summary>
namespace OutboundCall
{
    class Program
    {
        /// <summary>
        /// Application name. It can be an arbitrary, unique name for outbound call scenarios.
        /// </summary>
        private const string applicationName = "cvegso_outbound_cc";

        /// <summary>
        /// The IPv4 address of the FreePBX/Asterisk instance.
        /// </summary>
        private const string asteriskIPv4 = "10.168.3.224";

        /// <summary>
        /// The web server port on the FreePBX/Asterisk instance exposing ARI API.
        /// </summary>
        private const int asteriskWebPort = 8088;

        /// <summary>
        /// A valid Asterisk Web Interface User under which the application will run.
        /// </summary>
        private const string accountName = "cvegso";
        private const string accountPassword = "K1skop1";

        /// <summary>
        /// A valid SIP extension. It will be dialed out as the customer.
        /// </summary>
        private const string customerUri = "SIP/4448";

        /// <summary>
        /// A valid SIP extension. It will be dialed out as the agent.
        /// </summary>
        private const string agentUri = "SIP/4449";

        /// <summary>
        /// The built-in FreePBX/Asterisk sound file used as welcome message.
        /// </summary>
        private const string welcomeMessageSoundFile = "sound:dir-welcome";

        private static AriClient ariClient = null;
        private static Channel customerChannel = null;
        private static Channel agentChannel = null;
        private static Bridge bridge = null;
        private static Playback welcomeMessage = null;

        static void Main(string[] args)
        {
            string logLabel = nameof(Main);

            SetupAriClient();

            /*
             * It is an outbound scenario, let's start by dialing the customer.
             */

            customerChannel = Dial(customerUri);

            Console.WriteLine($"{logLabel} - Press 'q' to quit ...");

            while (Console.ReadKey().KeyChar != 'q');

            /*
             * Cleaning up resources.
             */

            TerminateCalls();
            DestroyAriClient();
        }

        /// <summary>
        /// It sets up the ARI client and subscribes to the necessary events.
        /// </summary>
        private static void  SetupAriClient()
        {
            string logLabel = nameof(SetupAriClient);

            ariClient = new AriClient(new StasisEndpoint(asteriskIPv4, asteriskWebPort, accountName, accountPassword), applicationName);

            ariClient.Connect();

            while (!ariClient.Connected)
            {
                Console.WriteLine($"{logLabel} - Waiting for the ARI client to connect to Asterisk ...");
                Thread.Sleep(1000);
            }

            Console.WriteLine($"{logLabel} - ARI client connected. ConnectionState: {ariClient.ConnectionState}");

            ariClient.OnDialEvent += AriClient_OnDialEvent;
            ariClient.OnChannelStateChangeEvent += AriClient_OnChannelStateChangeEvent;
            ariClient.OnPlaybackFinishedEvent += AriClient_OnPlaybackFinishedEvent;
        }

        /// <summary>
        /// It usubscribes from the events and destroys the ARI client.
        /// </summary>
        private static void DestroyAriClient()
        {
            ariClient.OnDialEvent -= AriClient_OnDialEvent;
            ariClient.OnChannelStateChangeEvent -= AriClient_OnChannelStateChangeEvent;
            ariClient.OnPlaybackFinishedEvent -= AriClient_OnPlaybackFinishedEvent;

            ariClient.Disconnect();

            ariClient = null;
        }

        /// <summary>
        /// Event handler invoked when the call initiated by Dial() progresses.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void AriClient_OnDialEvent(IAriClient sender, AsterNet.Standard.Models.DialEvent e)
        {
            string logLabel = nameof(AriClient_OnDialEvent);

            try
            {
                Console.WriteLine($"{logLabel} - Dialstring: {e.Dialstring}, Dialstatus: {e.Dialstatus}");

                if (e.Dialstatus == "ANSWER")
                {
                    if (e.Peer.Id == customerChannel.Id)
                    {
                        /*
                         * Customer answered the call. Let's start playing the welcome message.
                         */

                        welcomeMessage = PlayP2PMessage(customerChannel, welcomeMessageSoundFile);
                    }
                    else if (e.Peer.Id == agentChannel.Id)
                    {
                        /*
                         * Agent answered the call. Let's join the agent to the customer
                         * via the already existing bridge and let's stop playing the music 
                         * on the bridge.
                         * 
                         * NOTE: MoH is played to the customer via the bridge while the agent 
                         * is being dialed.
                         */

                        AddChannelToBridge(bridge, agentChannel);

                        StopPlayingMusicOnBridge(bridge);

                        /*
                         * Let's start recording the customer <=> agent conversation via the
                         * bridge.
                         */

                        StartRecordingOnBridge(bridge);
                    }
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to handle the event. Reason: {ex.Message}");
            }
        }

        /// <summary>
        /// Event handler invoked the playing the audio message changes state.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void AriClient_OnPlaybackFinishedEvent(IAriClient sender, PlaybackFinishedEvent e)
        {
            string logLabel = nameof(AriClient_OnPlaybackFinishedEvent);

            try
            {
                Console.WriteLine($"{logLabel} - State: {e.Playback.State}");

                if (e.Playback.State == "done" && e.Playback.Id == welcomeMessage.Id)
                {
                    /*
                     * Playing the welcome message to the customer just finished.
                     * 
                     * Let's 
                     *    1) create a bridge 
                     *    2) add the customer to the bridge
                     *    3) start playing musing on hold to the customer via the bridge
                     *    4) dial the agent in the background
                     */

                    bridge = CreateBridge("mixing,proxy_media");

                    AddChannelToBridge(bridge, customerChannel);

                    StartPlayingMusicOnBridge(bridge);

                    agentChannel = Dial(agentUri);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to handle the event. Reason: {ex.Message}");
            }
        }

        /// <summary>
        /// Event handler invoked when a channel changes state.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void AriClient_OnChannelStateChangeEvent(IAriClient sender, AsterNet.Standard.Models.ChannelStateChangeEvent e)
        {           
            string logLabel = nameof(AriClient_OnChannelStateChangeEvent);

            Console.WriteLine($"{logLabel} - ChannelId: {e.Channel.Id}, ChannelState: {e.Channel.State}");
        }

        /// <summary>
        /// It dials the specified party/uri.
        /// </summary>
        /// <param name="uri"></param>
        /// <returns></returns>
        private static Channel Dial(string uri)
        {
            string logLabel = nameof(Dial);

            Console.WriteLine($"{logLabel} - Dialing uri: {uri}");

            Channel channel = ariClient.Channels.Create(uri, applicationName);

            ariClient.Channels.Dial(channel.Id);

            return channel;
        }

        /// <summary>
        /// It starts playing the specified message to the remote party of the specified call.
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="media"></param>
        /// <returns></returns>
        private static Playback PlayP2PMessage(Channel channel, string media)
        {
            string logLabel = nameof(PlayP2PMessage);

            Console.WriteLine($"{logLabel} - Playing media: {media} to channel: {channel.Id}");

            /*
             * media options: e.g. sound:xxx, tone:xxx, sound:http://xxx, digits:xxx
             */
            Playback playback = ariClient.Channels.PlayWithId(channel.Id, Guid.NewGuid().ToString(), media);

            return playback;
        }

        /// <summary>
        /// It creates a bridge via which customer and agent can be connected and
        /// their conversation can be recorded.
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private static Bridge CreateBridge(string type)
        {
            string logLabel = nameof(CreateBridge);

            Console.WriteLine($"{logLabel} - Creating bridge with type: {type}");

            Bridge bridge = ariClient.Bridges.Create(type);

            return bridge;
        }

        /// <summary>
        /// It adds the specified channel to the specified bridge.
        /// </summary>
        /// <param name="bridge"></param>
        /// <param name="channel"></param>
        private static void AddChannelToBridge(Bridge bridge, Channel channel)
        {
            string logLabel = nameof(AddChannelToBridge);

            Console.WriteLine($"{logLabel} - Adding channel: {channel.Id} to the bridge: {bridge.Id}");

            ariClient.Bridges.AddChannel(bridge.Id, channel.Id);
        }

        /// <summary>
        /// It starts playing music via the bridge.
        /// </summary>
        /// <param name="bridge"></param>
        private static void StartPlayingMusicOnBridge(Bridge bridge)
        {
            string logLabel = nameof(StartPlayingMusicOnBridge);

            Console.WriteLine($"{logLabel} - Starting to play MoH on the bridge: {bridge.Id}");

            ariClient.Bridges.StartMoh(bridge.Id);
        }

        /// <summary>
        /// It stops playing music via the bridge.
        /// </summary>
        /// <param name="bridge"></param>
        private static void StopPlayingMusicOnBridge(Bridge bridge)
        {
            string logLabel = nameof(StopPlayingMusicOnBridge);

            Console.WriteLine($"{logLabel} - Stopping to play MoH on the bridge: {bridge.Id}");

            ariClient.Bridges.StopMoh(bridge.Id);
        }

        /// <summary>
        /// It starts call recording via the bridge.
        /// </summary>
        /// <param name="bridge"></param>
        /// <returns></returns>
        private static string StartRecordingOnBridge(Bridge bridge)
        {
            string logLabel = nameof(StartRecordingOnBridge);

            try
            {
                string recordingId = Guid.NewGuid().ToString("N");

                Console.WriteLine($"{logLabel} - Starting call recording on the bridge: {bridge.Id}");

                ariClient.Bridges.Record(bridge.Id, recordingId, "wav", beep: true);

                Console.WriteLine($"{logLabel} - Recording is started with name: {recordingId}");

                return recordingId;
            }
            catch(Exception ex)
            {
                /*
                 * NOTE: Asterisk stores recording into /var/spool/asterisk/recording by default.
                 * Make sure this directory exists and owned by the user 'asterisk' if Record() 
                 * raises exception with HTTP 500 error.
                 */

                Console.WriteLine($"{logLabel} - Failed to start recording. Reason: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// It terminates the customer/agent calls and the bridge.
        /// </summary>
        private static void TerminateCalls()
        {
            string logLabel = nameof(TerminateCalls);

            Console.WriteLine($"{logLabel} - Destroying calls and the bridge");

            TerminateChannel(customerChannel);
            TerminateChannel(agentChannel);
            DestroyBridge(bridge);

            customerChannel = null;
            agentChannel = null;
            bridge = null;
        }

        /// <summary>
        /// It terminates the specified channel/call.
        /// </summary>
        /// <param name="channel"></param>
        private static void TerminateChannel(Channel channel)
        {
            string logLabel = nameof(TerminateChannel);

            try
            {
                Console.WriteLine($"{logLabel} - Terminating channel: {channel.Id}");

                ariClient.Channels.Hangup(channel.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to terminate channel. Reason: {ex.Message}");
            }
        }

        /// <summary>
        /// It destroys the specified bridge.
        /// </summary>
        /// <param name="bridge"></param>
        private static void DestroyBridge(Bridge bridge)
        {
            string logLabel = nameof(DestroyBridge);

            try
            {
                Console.WriteLine($"{logLabel} - Destroying bridge: {bridge.Id}");

                ariClient.Bridges.Destroy(bridge.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to destroy bridge. Reason: {ex.Message}");
            }
        }
    }
}
