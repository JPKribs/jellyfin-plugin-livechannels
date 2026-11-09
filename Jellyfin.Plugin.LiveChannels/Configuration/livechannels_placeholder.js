// MARK: Placeholder blocks (shared by the Channels and Popular pages)

export var PLACEHOLDER_STYLES = [
    { value: 'Default', label: 'Default' },
    { value: 'Static', label: 'Static' },
    { value: 'Error', label: 'Error' },
    { value: 'Ocean', label: 'Ocean' },
    { value: 'NightSky', label: 'Night sky' },
    { value: 'LavaLamp', label: 'Lava lamp' },
    { value: 'Rain', label: 'Rain' },
    { value: 'Embers', label: 'Embers' },
    { value: 'Aurora', label: 'Aurora' },
    { value: 'Custom', label: 'Custom' }
];

export var DEFAULT_NAME = '-';
export var DEFAULT_TITLE = 'Thank you for watching!';
export var DEFAULT_BODY = 'We will resume at {time} with {contentName}.';
var MAX_TEXT = 200;

// MARK: Time helpers (shared with the rating block cards)

function pad2(n) { return (n < 10 ? '0' : '') + n; }

export function minutesToTime(m) {
    m = ((m % 1440) + 1440) % 1440;
    return pad2(Math.floor(m / 60)) + ':' + pad2(m % 60);
}

export function timeToMinutes(text) {
    var parts = (text || '').split(':');
    var mins = ((parseInt(parts[0], 10) || 0) * 60) + (parseInt(parts[1], 10) || 0);
    return ((mins % 1440) + 1440) % 1440;
}

// MARK: newPlaceholderBlock

export function newPlaceholderBlock() {
    return {
        IsPlaceholder: true,
        MinOfficialRating: '',
        MaxOfficialRating: '',
        IncludeUnrated: true,
        IsKids: false,
        Period: 'Custom',
        StartMinutes: 0,
        EndMinutes: 360,
        PlaceholderName: DEFAULT_NAME,
        PlaceholderStyle: 'Default',
        PlaceholderTitle: DEFAULT_TITLE,
        PlaceholderBody: DEFAULT_BODY,
        PlaceholderMediaPath: ''
    };
}

// MARK: buildPlaceholderCard

export function buildPlaceholderCard(block, onRemove) {
    var card = document.createElement('div');
    card.className = 'lc-ratingblock lc-placeholderblock';
    var styleOptions = PLACEHOLDER_STYLES.map(function (s) {
        return '<option value="' + s.value + '">' + s.label + '</option>';
    }).join('');
    card.innerHTML =
        '<div class="lc-source-header">' +
            '<span class="material-icons lc-source-icon" aria-hidden="true">tv_off</span>' +
            '<span class="lc-block-title">Placeholder block</span>' +
            '<button is="emby-button" type="button" class="lc-remove raised jpk-icon-btn jpk-button-destructive" title="Remove"><span class="material-icons" aria-hidden="true">delete</span><span>Remove</span></button>' +
        '</div>' +
        '<div class="lc-block-times">' +
            '<div class="inputContainer"><label class="inputLabel">Start time</label><input is="emby-input" type="time" class="lc-ph-start" /></div>' +
            '<div class="inputContainer"><label class="inputLabel">End time</label><input is="emby-input" type="time" class="lc-ph-end" /></div>' +
        '</div>' +
        '<div class="inputContainer"><label class="inputLabel">Guide name</label>' +
            '<input is="emby-input" type="text" class="lc-ph-name" maxlength="' + MAX_TEXT + '" />' +
            '<div class="fieldDescription">What the guide calls this window.</div></div>' +
        '<div class="selectContainer"><label class="selectLabel">Style</label>' +
            '<select class="lc-ph-style jpk-selector-dropdown">' + styleOptions + '</select></div>' +
        '<div class="inputContainer lc-ph-media-row"><label class="inputLabel">Image, GIF or video</label>' +
            '<input is="emby-input" type="text" class="lc-ph-media" placeholder="/path/on/the/server/card.mp4" />' +
            '<div class="fieldDescription">A path on the server. A GIF or video should be exactly 10 seconds long, ending where it starts, so it loops cleanly. Its sound is not played.</div></div>' +
        '<div class="inputContainer"><label class="inputLabel">Title</label>' +
            '<input is="emby-input" type="text" class="lc-ph-title" maxlength="' + MAX_TEXT + '" /></div>' +
        '<div class="inputContainer"><label class="inputLabel">Message</label>' +
            '<input is="emby-input" type="text" class="lc-ph-body" maxlength="' + MAX_TEXT + '" />' +
            '<div class="fieldDescription">{time} becomes the time the channel resumes and {contentName} what it resumes with. Leave the title or message empty to leave it off.</div></div>';

    var start = card.querySelector('.lc-ph-start');
    var end = card.querySelector('.lc-ph-end');
    var name = card.querySelector('.lc-ph-name');
    var style = card.querySelector('.lc-ph-style');
    var mediaRow = card.querySelector('.lc-ph-media-row');
    var media = card.querySelector('.lc-ph-media');
    var title = card.querySelector('.lc-ph-title');
    var body = card.querySelector('.lc-ph-body');

    block.Period = 'Custom';
    start.value = minutesToTime(block.StartMinutes || 0);
    end.value = minutesToTime(block.EndMinutes || 0);
    name.value = block.PlaceholderName == null ? DEFAULT_NAME : block.PlaceholderName;
    style.value = block.PlaceholderStyle || 'Default';
    media.value = block.PlaceholderMediaPath || '';
    title.value = block.PlaceholderTitle == null ? DEFAULT_TITLE : block.PlaceholderTitle;
    body.value = block.PlaceholderBody == null ? DEFAULT_BODY : block.PlaceholderBody;

    function syncMedia() { mediaRow.classList.toggle('hidden', style.value !== 'Custom'); }
    syncMedia();

    start.addEventListener('change', function () { block.StartMinutes = timeToMinutes(start.value); });
    end.addEventListener('change', function () { block.EndMinutes = timeToMinutes(end.value); });
    name.addEventListener('input', function () { block.PlaceholderName = name.value; });
    name.addEventListener('change', function () {
        if (!name.value.trim()) { name.value = DEFAULT_NAME; block.PlaceholderName = DEFAULT_NAME; }
    });
    style.addEventListener('change', function () { block.PlaceholderStyle = style.value; syncMedia(); });
    media.addEventListener('input', function () { block.PlaceholderMediaPath = media.value.trim(); });
    title.addEventListener('input', function () { block.PlaceholderTitle = title.value; });
    body.addEventListener('input', function () { block.PlaceholderBody = body.value; });

    card.querySelector('.lc-remove').addEventListener('click', onRemove);
    return card;
}
