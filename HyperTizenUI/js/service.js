// Tizen 2.4+ forbids a Web application from launching a service application
// that belongs to another package. HyperTizenUI is a TizenBrew module while
// the .NET service is a separately signed TPK, so application.launch() always
// produces NotSupportedError on current Samsung TVs. The service is installed
// and started through Tizen/SDB; this module only connects to its WebSockets.
try {
    tizen.application.getAppInfo('io.gh.reisxd.HyperTizen');
    console.log('HyperTizen service package is installed; waiting for WebSocket connection');
} catch (e) {
    console.log('HyperTizen service package is not installed');
}
