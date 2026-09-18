// Web Speech API bridge for the Player Entry Sheet's mic button.
// Chrome and Edge only; Firefox and Safari have no SpeechRecognition.
window.rsSpeech = (function () {
    const Recognizer = window.SpeechRecognition || window.webkitSpeechRecognition;

    let recognition = null;
    let dotNetRef = null;

    // Chrome ends a session on its own after a stretch of silence. This tracks
    // whether the *user* still wants the mic on, so onend can restart it.
    let wantListening = false;

    function isSupported() {
        return !!Recognizer;
    }

    function start(ref) {
        if (!Recognizer) return false;

        dotNetRef = ref;
        wantListening = true;

        if (recognition) return true;

        recognition = new Recognizer();
        recognition.lang = 'en-US';
        recognition.continuous = true;
        recognition.interimResults = true;

        recognition.onresult = function (e) {
            // resultIndex skips the results already reported
            for (let i = e.resultIndex; i < e.results.length; i++) {
                const result = e.results[i];
                dotNetRef.invokeMethodAsync('OnSpeechResult',
                    result[0].transcript, result.isFinal);
            }
        };

        recognition.onerror = function (e) {
            // Routine in continuous mode — onend restarts from here
            if (e.error === 'no-speech' || e.error === 'aborted') return;

            wantListening = false;
            if (dotNetRef) dotNetRef.invokeMethodAsync('OnSpeechError', e.error);
        };

        recognition.onend = function () {
            if (!wantListening) {
                finish();
                return;
            }

            // Restarting inside onend throws, so let the event settle first
            setTimeout(function () {
                if (!wantListening || !recognition) return;
                try {
                    recognition.start();
                } catch (err) {
                    finish();
                }
            }, 150);
        };

        try {
            recognition.start();
        } catch (err) {
            recognition = null;
            wantListening = false;
            return false;
        }

        return true;
    }

    function stop() {
        wantListening = false;
        if (recognition) recognition.stop();
    }

    function finish() {
        recognition = null;
        wantListening = false;
        if (dotNetRef) dotNetRef.invokeMethodAsync('OnSpeechEnd');
    }

    return { isSupported: isSupported, start: start, stop: stop };
})();
