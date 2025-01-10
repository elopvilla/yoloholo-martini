using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.MagicLeap;

/// <summary>
/// A script that enables and disables the RGB camera using the async methods.
/// </summary>
public class MagicLeapRGBCamera : MonoBehaviour
{
    /// <summary>
    /// Can be used by external scripts to query the status of the camera and see if the camera capture has been started.
    /// </summary>
    public bool IsCameraConnected => _captureCamera != null && _captureCamera.ConnectionEstablished;

    [SerializeField]
    [Tooltip("If true, the camera capture will start immediately.")]
    private bool _startCameraCaptureOnStart = true;

    [SerializeField, Tooltip("The UI to show the camera capture in JPEG format")]
    private RawImage _screenRendererRGBA = null;

    private Texture2D _rawVideoTextureRGBA;

    #region Capture Config

    private int _targetImageWidth = 1920;
    private int _targetImageHeight = 1080;
    private MLCameraBase.Identifier _cameraIdentifier = MLCameraBase.Identifier.CV;
    private MLCameraBase.CaptureFrameRate _targetFrameRate = MLCameraBase.CaptureFrameRate._30FPS;
    private MLCameraBase.OutputFormat _outputFormat = MLCameraBase.OutputFormat.RGBA_8888;



    #endregion

    #region Magic Leap Camera Info
    //The connected Camera
    private MLCamera _captureCamera;
    // True if CaptureVideoStartAsync was called successfully
    private bool _isCapturingVideo = false;
    #endregion

    private bool? _cameraPermissionGranted;
    private bool _isCameraInitializationInProgress;

    private readonly MLPermissions.Callbacks _permissionCallbacks = new MLPermissions.Callbacks();

    private void Awake()
    {
        _permissionCallbacks.OnPermissionGranted += OnPermissionGranted;
        _permissionCallbacks.OnPermissionDenied += OnPermissionDenied;
        _permissionCallbacks.OnPermissionDeniedAndDontAskAgain += OnPermissionDenied;
        _isCapturingVideo = false;
    }

    void Start()
    {
        if (_startCameraCaptureOnStart)
        {
            StartCameraCapture();
        }
    }

    /// <summary>
    /// Starts the Camera capture with the target settings.
    /// </summary>
    /// <param name="cameraIdentifier">Which camera to use. (Main or CV)</param>
    /// <param name="width">The width of the video stream.</param>
    /// <param name="height">The height of the video stream.</param>
    /// <param name="onCameraCaptureStarted">An action callback that returns true if the video capture started successfully.</param>
    public void StartCameraCapture(MLCameraBase.Identifier cameraIdentifier = MLCameraBase.Identifier.CV, int width = 1920, int height = 1080, Action<bool> onCameraCaptureStarted = null)
    {
        if (_isCameraInitializationInProgress)
        {
            Debug.LogError("Camera Initialization is already in progress.");
            onCameraCaptureStarted?.Invoke(false);
            return;
        }

        this._cameraIdentifier = cameraIdentifier;
        _targetImageWidth = width;
        _targetImageHeight = height;
        TryEnableMLCamera(onCameraCaptureStarted);
    }

    private void OnDisable()
    {
        _ = DisconnectCameraAsync();
    }

    private void OnPermissionGranted(string permission)
    {
        if (permission == MLPermission.Camera)
        {
            _cameraPermissionGranted = true;
            Debug.Log($"Granted {permission}.");
        }
    }

    private void OnPermissionDenied(string permission)
    {
        if (permission == MLPermission.Camera)
        {
            _cameraPermissionGranted = false;
            Debug.LogError($"{permission} denied, camera capture won't function.");
        }
    }

    private async void TryEnableMLCamera(Action<bool> onCameraCaptureStarted = null)
    {
        // If the camera initialization is already in progress, return immediately
        if (_isCameraInitializationInProgress)
        {
            onCameraCaptureStarted?.Invoke(false);
            return;
        }

        _isCameraInitializationInProgress = true;

        _cameraPermissionGranted = null;
        Debug.Log("Requesting Camera permission.");
        MLPermissions.RequestPermission(MLPermission.Camera, _permissionCallbacks);

        while (!_cameraPermissionGranted.HasValue)
        {
            // Wait until we have permission to use the camera
            await Task.Delay(TimeSpan.FromSeconds(1.0f));
        }

        if (MLPermissions.CheckPermission(MLPermission.Camera).IsOk || _cameraPermissionGranted.GetValueOrDefault(false))
        {
            Debug.Log("Initializing camera.");
            bool isCameraAvailable = await WaitForCameraAvailabilityAsync();

            if (isCameraAvailable)
            {
                await ConnectAndConfigureCameraAsync();
            }
        }

        _isCameraInitializationInProgress = false;
        onCameraCaptureStarted?.Invoke(_isCapturingVideo);
    }

    /// <summary>
    /// Connects the MLCamera component and instantiates a new instance
    /// if it was never created.
    /// </summary>
    private async Task<bool> WaitForCameraAvailabilityAsync()
    {
        bool cameraDeviceAvailable = false;
        int maxAttempts = 10;
        int attempts = 0;

        while (!cameraDeviceAvailable && attempts < maxAttempts)
        {
            MLResult result =
                MLCameraBase.GetDeviceAvailabilityStatus(_cameraIdentifier, out cameraDeviceAvailable);

            if (result.IsOk == false && cameraDeviceAvailable == false)
            {
                // Wait until the camera device is available
                await Task.Delay(TimeSpan.FromSeconds(1.0f));
            }
            attempts++;
        }

        return cameraDeviceAvailable;
    }

    private async Task<bool> ConnectAndConfigureCameraAsync()
    {
        Debug.Log("Starting Camera Capture.");

        MLCameraBase.ConnectContext context = CreateCameraContext();

        _captureCamera = await MLCamera.CreateAndConnectAsync(context);
        if (_captureCamera == null)
        {
            Debug.LogError("Could not create or connect to a valid camera. Stopping Capture.");
            return false;
        }

        Debug.Log("Camera Connected.");

        bool hasImageStreamCapabilities = GetStreamCapabilityWBestFit(out MLCameraBase.StreamCapability streamCapability);
        if (!hasImageStreamCapabilities)
        {
            Debug.LogError("Could not start capture. No valid Image Streams available. Disconnecting Camera.");
            await DisconnectCameraAsync();
            return false;
        }

        Debug.Log("Preparing camera configuration.");

        // Try to configure the camera based on our target configuration values
        MLCameraBase.CaptureConfig captureConfig = CreateCaptureConfig(streamCapability);
        var prepareResult = _captureCamera.PrepareCapture(captureConfig, out MLCameraBase.Metadata _);
        if (!MLResult.DidNativeCallSucceed(prepareResult.Result, nameof(_captureCamera.PrepareCapture)))
        {
            Debug.LogError($"Could not prepare capture. Result: {prepareResult.Result} .  Disconnecting Camera.");
            await DisconnectCameraAsync();
            return false;
        }

        Debug.Log("Starting Video Capture.");

        bool captureStarted = await StartVideoCaptureAsync();
        if (!captureStarted)
        {
            Debug.LogError("Could not start capture. Disconnecting Camera.");
            await DisconnectCameraAsync();
            return false;
        }

        return _isCapturingVideo;
    }

    private MLCameraBase.ConnectContext CreateCameraContext()
    {
        var context = MLCameraBase.ConnectContext.Create();
        context.CamId = _cameraIdentifier;
        context.Flags = MLCameraBase.ConnectFlag.CamOnly;
        return context;
    }

    private MLCameraBase.CaptureConfig CreateCaptureConfig(MLCameraBase.StreamCapability streamCapability)
    {
        var captureConfig = new MLCameraBase.CaptureConfig();
        captureConfig.CaptureFrameRate = _targetFrameRate;
        captureConfig.StreamConfigs = new MLCameraBase.CaptureStreamConfig[1];
        captureConfig.StreamConfigs[0] = MLCameraBase.CaptureStreamConfig.Create(streamCapability, _outputFormat);
        return captureConfig;
    }

    private async Task<bool> StartVideoCaptureAsync()
    {
        // Trigger auto exposure and white balance
        await _captureCamera.PreCaptureAEAWBAsync();

        var startCapture = await _captureCamera.CaptureVideoStartAsync();
        _isCapturingVideo = MLResult.DidNativeCallSucceed(startCapture.Result, nameof(_captureCamera.CaptureVideoStart));

        if (!_isCapturingVideo)
        {
            Debug.LogError($"Could not start camera capture. Result : {startCapture.Result}");
            return false;
        }

        _captureCamera.OnRawVideoFrameAvailable += OnCaptureRawVideoFrameAvailable;
        return true;
    }

    private async Task DisconnectCameraAsync()
    {
        if (_captureCamera != null)
        {
            if (_isCapturingVideo)
            {
                await _captureCamera.CaptureVideoStopAsync();
                _captureCamera.OnRawVideoFrameAvailable -= OnCaptureRawVideoFrameAvailable;
            }

            await _captureCamera.DisconnectAsync();
            _captureCamera = null;
        }

        _isCapturingVideo = false;
    }

    /// <summary>
    /// Gets the Image stream capabilities.
    /// </summary>
    /// <returns>True if MLCamera returned at least one stream capability.</returns>
    private bool GetStreamCapabilityWBestFit(out MLCameraBase.StreamCapability streamCapability)
    {
        streamCapability = default;

        if (_captureCamera == null)
        {
            Debug.Log("Could not get Stream capabilities Info. No Camera Connected");
            return false;
        }

        MLCameraBase.StreamCapability[] streamCapabilities =
            MLCameraBase.GetImageStreamCapabilitiesForCamera(_captureCamera, MLCameraBase.CaptureType.Video);

        if (streamCapabilities.Length <= 0)
            return false;

        if (MLCameraBase.TryGetBestFitStreamCapabilityFromCollection(streamCapabilities, _targetImageWidth,
                _targetImageHeight, MLCameraBase.CaptureType.Video,
                out streamCapability))
        {
            Debug.Log($"Stream: {streamCapability} selected with best fit.");
            return true;
        }

        Debug.Log($"No best fit found. Stream: {streamCapabilities[0]} selected by default.");
        streamCapability = streamCapabilities[0];
        return true;
    }

    private void OnCaptureRawVideoFrameAvailable(MLCameraBase.CameraOutput cameraOutput,
        MLCameraBase.ResultExtras resultExtras,
        MLCameraBase.Metadata metadata)
    {
        //Cache or use camera data as needed
        //TODO: Implement use of camera data
        if (cameraOutput.Format == MLCamera.OutputFormat.RGBA_8888)
        {
            //Flips the frame vertically so it does not appear upside down.
            MLCamera.FlipFrameVertically(ref cameraOutput);
            UpdateRGBTexture(cameraOutput.Planes[0]);
        }
    }

    private void UpdateRGBTexture(MLCamera.PlaneInfo imagePlane)
    {
        int actualWidth = (int)(imagePlane.Width * imagePlane.PixelStride);

        if (_rawVideoTextureRGBA != null &&
            (_rawVideoTextureRGBA.width != imagePlane.Width || _rawVideoTextureRGBA.height != imagePlane.Height))
        {
            Destroy(_rawVideoTextureRGBA);
            _rawVideoTextureRGBA = null;
        }

        if (_rawVideoTextureRGBA == null)
        {
            // Create a new texture that will display the RGB image
            _rawVideoTextureRGBA = new Texture2D((int)imagePlane.Width, (int)imagePlane.Height, TextureFormat.RGBA32, false);
            _rawVideoTextureRGBA.filterMode = FilterMode.Bilinear;

            // Assign the RawImage Texture to the resulting texture
            _screenRendererRGBA.texture = _rawVideoTextureRGBA;
        }

        // Image width and stride may differ due to padding bytes for memory alignment. Skip over padding bytes when accessing pixel data.
        if (imagePlane.Stride != actualWidth)
        {
            // Create a new array to store the pixel data without padding
            var newTextureChannel = new byte[actualWidth * imagePlane.Height];
            // Loop through each row of the image
            for (int i = 0; i < imagePlane.Height; i++)
            {
                // Copy the pixel data from the original array to the new array, skipping the padding bytes
                Buffer.BlockCopy(imagePlane.Data, (int)(i * imagePlane.Stride), newTextureChannel, i * actualWidth, actualWidth);
            }
            // Load the new array as the texture data
            _rawVideoTextureRGBA.LoadRawTextureData(newTextureChannel);
        }
        else // If the stride is equal to the width, no padding bytes are present
        {
            _rawVideoTextureRGBA.LoadRawTextureData(imagePlane.Data);
        }

        _rawVideoTextureRGBA.Apply();
    }

    public Texture2D GetImage()
    {
        return _rawVideoTextureRGBA;
    }
}