using System;
using System.Collections;
using System.Collections.Generic;
using Convai.Scripts.Utils;
using Grpc.Core;
using Service;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Logger = Convai.Scripts.Utils.Logger;

namespace Convai.Scripts
{
    [RequireComponent(typeof(Animator), typeof(AudioSource))]
    [AddComponentMenu("Convai/ConvaiNPC")]
    [HelpURL(
        "https://docs.convai.com/api-docs/plugins-and-integrations/unity-plugin/overview-of-the-convainpc.cs-script")]
    public class ConvaiNPC : MonoBehaviour
    {
        private const int AUDIO_SAMPLE_RATE = 44100;
        private const string GRPC_API_ENDPOINT = "stream.convai.com";
        private const int RECORDING_FREQUENCY = AUDIO_SAMPLE_RATE;
        private const int RECORDING_LENGTH = 30;
        private static readonly int Talk = Animator.StringToHash("Talk");

        [Header("Character Information")]
        [Tooltip("Enter the character name for this NPC.")]
        public string characterName;

        [Tooltip("Enter the character ID for this NPC.")]
        public string characterID;

        [Tooltip("The current session ID for the chat with this NPC.")]
        [ReadOnly]
        public string sessionID = "-1";

        [Tooltip("Is this character active?")]
        [ReadOnly]
        public bool isCharacterActive;
        [HideInInspector] public ConvaiActionsHandler actionsHandler;

        [Tooltip("Is this character talking?")]
        [SerializeField]
        [ReadOnly]
        private bool isCharacterTalking;

        [Header("Session Initialization")]
        [Tooltip("Enable/disable initializing session ID by sending a text request to the server")]
        public bool initializeSessionID = true;

        [HideInInspector] public NarrativeDesignManager narrativeDesignManager;
        [HideInInspector] public TriggerUnityEvent onTriggerSent;
        private readonly Queue<GetResponseResponse> _getResponseResponses = new();
        private Channel _channel;
        private ConvaiService.ConvaiServiceClient _client;
        private DialogueHandler dialogueHandler;
        private TMP_InputField _currentInputField;
        private ConvaiGRPCAPI _grpcAPI;
        private bool _isActionActive;
        private bool _isLipSyncActive;
        private bool _stopAudioPlayingLoop;
        private bool _stopHandlingInput;
        public ActionConfig ActionConfig;

        public bool IsCharacterTalking
        {
            get => isCharacterTalking;
            private set => isCharacterTalking = value;
        }

        public string GetEndPointURL => GRPC_API_ENDPOINT;

        // Properties with getters and setters
        [field: NonSerialized] public bool IncludeActionsHandler { get; set; }
        [field: NonSerialized] public bool LipSync { get; set; }
        [field: NonSerialized] public bool HeadEyeTracking { get; set; }
        [field: NonSerialized] public bool EyeBlinking { get; set; }
        [field: NonSerialized] public bool NarrativeDesignManager { get; set; }
        [field: NonSerialized] public bool ConvaiGroupNPCController { get; set; }

        public ConvaiNPCAudioManager AudioManager { get; private set; }


        private void Awake()
        {
            Logger.Info("Initializing ConvaiNPC : " + characterName, Logger.LogCategory.Character);
            InitializeComponents();
            Logger.Info("ConvaiNPC component initialized", Logger.LogCategory.Character);
        }

        private async void Start()
        {
            // Assign the ConvaiGRPCAPI component in the scene
            _grpcAPI = ConvaiGRPCAPI.Instance;

            // Start the coroutine that plays audio clips in order
            StartCoroutine(AudioManager.PlayAudioInOrder());
            InvokeRepeating(nameof(ProcessResponse), 0f, 1 / 100f);

            SslCredentials credentials = new(); // Create SSL credentials for secure communication
            _channel = new Channel(GRPC_API_ENDPOINT, credentials); // Initialize a gRPC channel with the specified endpoint and credentials
            _client = new ConvaiService.ConvaiServiceClient(_channel); // Initialize the gRPC client for the ConvaiService using the channel

            if (initializeSessionID)
            {
                sessionID = await ConvaiGRPCAPI.InitializeSessionIDAsync(characterName, _client, characterID, sessionID);
            }
            dialogueHandler = DialogueHandler.Instance;
            //_convaiChatUIHandler = ConvaiChatUIHandler.Instance;
        }

        private void OnEnable()
        {
            AudioManager.OnCharacterTalkingChanged += SetCharacterTalking;
            AudioManager.OnAudioTranscriptAvailable += HandleAudioTranscriptAvailable;

            ConvaiNPCManager.Instance.OnActiveNPCChanged += HandleActiveNPCChanged;
        }


        private void OnDestroy()
        {
            if (AudioManager != null)
            {
                AudioManager.OnCharacterTalkingChanged -= SetCharacterTalking;
                AudioManager.OnAudioTranscriptAvailable -= HandleAudioTranscriptAvailable;
            }

            ConvaiNPCManager.Instance.OnActiveNPCChanged -= HandleActiveNPCChanged;
        }

        private void HandleAudioTranscriptAvailable(string transcript)
        {
            dialogueHandler.SendText(transcript);
        }

        /// <summary>
        ///     Unity callback that is invoked when the application is quitting.
        ///     Stops the loop that plays audio in order.
        /// </summary>
        private void OnApplicationQuit()
        {
            AudioManager.StopAudioLoop();
        }

        public async void TriggerEvent(string triggerName, string triggerMessage = "")
        {
            TriggerConfig trigger = new()
            {
                TriggerName = triggerName,
                TriggerMessage = triggerMessage
            };

            // Send the trigger to the server using GRPC
            await ConvaiGRPCAPI.Instance.SendTriggerData(_client, characterID, trigger);

            // Invoke the UnityEvent
            onTriggerSent.Invoke(triggerMessage, triggerName);
        }

        private event Action<bool> OnCharacterTalking;

        private void UpdateWaitUntilLipSync(bool value)
        {
            AudioManager.SetWaitForCharacterLipSync(value);
        }

        private void HandleActiveNPCChanged(ConvaiNPC newActiveNPC)
        {
            // If this NPC is no longer the active NPC, interrupt its speech
            if (this != newActiveNPC) InterruptCharacterSpeech();
        }


        private void InitializeComponents()
        {
            AudioManager = gameObject.AddComponent<ConvaiNPCAudioManager>();
            narrativeDesignManager = GetComponent<NarrativeDesignManager>();
            StartCoroutine(InitializeActionsHandler());
        }


        private IEnumerator InitializeActionsHandler()
        {
            yield return new WaitForSeconds(1);
            actionsHandler = GetComponent<ConvaiActionsHandler>();
            if (actionsHandler != null)
            {
                _isActionActive = true;
                ActionConfig = actionsHandler.ActionConfig;
            }
        }


        /// <summary>
        ///     Sends message data to the server asynchronously.
        /// </summary>
        /// <param name="text">The message to send.</param>
        public async void SendTextDataAsync(string text)
        {
            try
            {
                await ConvaiGRPCAPI.Instance.SendTextData(_client, text, characterID,
                    _isActionActive, true, ActionConfig, FaceModel.OvrModelName);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, Logger.LogCategory.Character);
                // Handle the exception, e.g., show a message to the user.
            }
        }

        /// <summary>
        ///     Initializes the session in an asynchronous manner and handles the receiving of results from the server.
        ///     Initiates the audio recording process using the gRPC API.
        /// </summary>
        public async void StartListening()
        {
            if (!MicrophoneManager.Instance.HasAnyMicrophoneDevices())
            {
                NotificationSystemHandler.Instance.NotificationRequest(NotificationType.NoMicrophoneDetected);
                return;
            }

            await _grpcAPI.StartRecordAudio(_client, _isActionActive, _isLipSyncActive, RECORDING_FREQUENCY,
                RECORDING_LENGTH, characterID, ActionConfig, FaceModel.OvrModelName); 
        }

        /// <summary>
        ///     Stops the ongoing audio recording process.
        /// </summary>
        public void StopListening()
        {
            // Stop the audio recording process using the ConvaiGRPCAPI StopRecordAudio method
            _grpcAPI.StopRecordAudio();
        }

        /// <summary>
        ///     Add response to the GetResponseResponse Queue
        /// </summary>
        /// <param name="response"></param>
        public void EnqueueResponse(GetResponseResponse response)
        {
            if (response == null || response.AudioResponse == null) return;
            Logger.DebugLog($"Adding Response for Processing: {response.AudioResponse.TextData}", Logger.LogCategory.LipSync);
            _getResponseResponses.Enqueue(response);
        }

        public void ClearResponseQueue()
        {
            _getResponseResponses.Clear();
        }

        /// <summary>
        ///     Processes a response fetched from a character.
        /// </summary>
        /// <remarks>
        ///     1. Processes audio/message/face data from the response and adds it to _responseAudios.
        ///     2. Identifies actions from the response and parses them for execution.
        /// </remarks>
        private void ProcessResponse()
        {
            // Check if the character is active and should process the response
            if (!isCharacterActive) return;
            if (_getResponseResponses.Count <= 0) return;
            GetResponseResponse getResponseResponse = _getResponseResponses.Dequeue();
            bool isDialogueOpen = GameManager.Instance.dialogueManager.isDialogueOpen;
            if (getResponseResponse?.AudioResponse == null || !isDialogueOpen) return;
            // Check if text data exists in the response
            if (getResponseResponse.AudioResponse.AudioData.ToByteArray().Length > 46)
            {
                // Initialize empty string for text
                string textDataString = getResponseResponse.AudioResponse.TextData;
                if (textDataString == "") return;

                byte[] byteAudio = getResponseResponse.AudioResponse.AudioData.ToByteArray();

                AudioClip clip = AudioManager.ProcessByteAudioDataToAudioClip(byteAudio,
                    getResponseResponse.AudioResponse.AudioConfig.SampleRateHertz.ToString());

                // Add the response audio along with associated data to the list
                AudioManager.AddResponseAudio(new ConvaiNPCAudioManager.ResponseAudio
                {
                    AudioClip = clip,
                    AudioTranscript = textDataString,
                    IsFinal = false
                });
            }
            else if (getResponseResponse.AudioResponse.EndOfResponse)
            {
                Logger.DebugLog("We have received end of response", Logger.LogCategory.LipSync);
                // Handle the case where there is a DebugLog but no audio response
                AudioManager.AddResponseAudio(new ConvaiNPCAudioManager.ResponseAudio
                {
                    AudioClip = null,
                    AudioTranscript = null,
                    IsFinal = true
                });
            }
        }

        public int GetAudioResponseCount()
        {
            return AudioManager.GetAudioResponseCount();
        }

        public void StopAllAudioPlayback()
        {
            AudioManager.StopAllAudioPlayback();
            AudioManager.ClearResponseAudioQueue();
        }


        public void SetCharacterTalking(bool isTalking)
        {
            if (IsCharacterTalking != isTalking)
            {
                Logger.Info($"Character {characterName} is talking: {isTalking}", Logger.LogCategory.Character);
                IsCharacterTalking = isTalking;
                OnCharacterTalking?.Invoke(IsCharacterTalking);
                if (!isTalking)
                { 
                    dialogueHandler.FinishResponse();
                }
            }
        }


        public void InterruptCharacterSpeech()
        {
            _grpcAPI.InterruptCharacterSpeech(this);
        }

        public ConvaiService.ConvaiServiceClient GetClient()
        {
            return _client;
        }

        public void UpdateSessionID(string newSessionID)
        {
            sessionID = newSessionID;
        }

        [Serializable]
        public class TriggerUnityEvent : UnityEvent<string, string>
        {
        }
    }
}