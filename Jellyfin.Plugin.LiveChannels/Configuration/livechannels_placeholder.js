// livechannels_placeholder.js
// The rating and placeholder block cards that the Channels and Popular pages share.

import { escapeHtml } from './configurationpage?name=livechannels_jpkribs_shared.js';

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
var nextId = 0;

// pad2
// Pads a number under 10 with a leading zero.
//
// Inputs
// n [number]: The number to pad.
//
// Output
// [string]: The padded number.
function pad2(n) { return (n < 10 ? '0' : '') + n; }

// uid
// Makes an element id unique on the page, so each card's labels point at their own fields.
//
// Inputs
// name [string]: The field name.
//
// Output
// [string]: The id.
function uid(name) { nextId += 1; return 'lcBlock' + name + nextId; }

// minutesToTime
// Turns minutes after midnight into the HH:MM a time input takes.
//
// Inputs
// m [number]: Minutes after midnight, wrapped to one day.
//
// Output
// [string]: The time as HH:MM.
export function minutesToTime(m) {
    m = ((m % 1440) + 1440) % 1440;
    return pad2(Math.floor(m / 60)) + ':' + pad2(m % 60);
}

// timeToMinutes
// Turns the HH:MM a time input gives into minutes after midnight.
//
// Inputs
// text [string]: The time as HH:MM.
//
// Output
// [number]: Minutes after midnight.
export function timeToMinutes(text) {
    var parts = (text || '').split(':');
    var mins = ((parseInt(parts[0], 10) || 0) * 60) + (parseInt(parts[1], 10) || 0);
    return ((mins % 1440) + 1440) % 1440;
}

// newRatingBlock
// Builds an empty rating block that allows every rating all day.
//
// Output
// [object]: The rating block.
export function newRatingBlock() {
    return { MinOfficialRating: '', MaxOfficialRating: '', IncludeUnrated: true, IsKids: false, Period: 'AllDay', StartMinutes: 0, EndMinutes: 0 };
}

// newPlaceholderBlock
// Builds a placeholder block with the default window, style, and message.
//
// Output
// [object]: The placeholder block.
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

// migrateRatingBlocks
// Shows a channel's single stored rating band as one all day block, so it stays visible and editable.
//
// Inputs
// owner [object]: The channel or Popular Channel settings.
//
// Output
// [object[]]: The blocks to edit.
export function migrateRatingBlocks(owner) {
    var blocks = owner.RatingBlocks || [];
    if (blocks.some(function (b) { return !b.IsPlaceholder; })) return blocks;
    if (owner.MinOfficialRating || owner.MaxOfficialRating || owner.IncludeUnrated === false) {
        return [{ MinOfficialRating: owner.MinOfficialRating || '', MaxOfficialRating: owner.MaxOfficialRating || '', IncludeUnrated: owner.IncludeUnrated !== false, Period: 'AllDay', StartMinutes: 0, EndMinutes: 0 }].concat(blocks);
    }
    return blocks;
}

// loadRatings
// Reads the server's parental ratings, sorted from lowest to highest score.
//
// Output
// [Promise<object>]: { options, valueOf }, where options is the option markup and valueOf maps a name to its score.
export function loadRatings() {
    return ApiClient.getJSON(ApiClient.getUrl('Localization/ParentalRatings')).then(function (list) {
        var seen = {};
        var ratings = [];
        (list || []).forEach(function (r) {
            var score = r.Value;
            if (score === undefined && r.RatingScore) score = r.RatingScore.Score;
            if (r && r.Name && !seen[r.Name]) { seen[r.Name] = true; ratings.push({ Name: r.Name, Value: score || 0 }); }
        });
        ratings.sort(function (a, b) { return a.Value - b.Value; });
        return ratingInfo(ratings);
    }).catch(function () {
        return ratingInfo([]);
    });
}

// ratingInfo
// Wraps a sorted rating list as the option markup and a score lookup.
//
// Inputs
// ratings [object[]]: { Name, Value } pairs.
//
// Output
// [object]: { options, valueOf }.
function ratingInfo(ratings) {
    return {
        options: ratings.map(function (r) {
            return '<option value="' + escapeHtml(r.Name) + '">' + escapeHtml(r.Name) + '</option>';
        }).join(''),
        valueOf: function (name) {
            if (!name) return null;
            for (var i = 0; i < ratings.length; i++) { if (ratings[i].Name === name) return ratings[i].Value; }
            return null;
        }
    };
}

// blockCard
// Builds the panel a block card sits in, with its icon, title, and Remove button.
//
// Inputs
// icon [string]: The material icon name.
// title [string]: The card title.
// body [string]: The markup of the fields.
// onRemove [function]: Called when Remove is selected.
//
// Output
// [HTMLElement]: The card.
function blockCard(icon, title, body, onRemove) {
    var card = document.createElement('div');
    card.className = 'base-panel';
    card.innerHTML =
        '<div class="base-panel-header"><div class="lc-block-head">' +
            '<span class="material-icons" aria-hidden="true">' + icon + '</span>' +
            '<span class="lc-block-title">' + title + '</span>' +
            '<button is="emby-button" type="button" class="raised base-button destructive small lc-remove"><span class="material-icons" aria-hidden="true">delete</span><span>Remove</span></button>' +
        '</div></div>' +
        '<div class="base-panel-body">' + body + '</div>';
    card.querySelector('.lc-remove').addEventListener('click', onRemove);
    return card;
}

// timeFields
// Builds the Start Time and End Time pair on one row.
//
// Inputs
// startClass [string]: The class of the start input.
// endClass [string]: The class of the end input.
//
// Output
// [string]: The markup.
function timeFields(startClass, endClass) {
    var startId = uid('Start');
    var endId = uid('End');
    return '<div class="base-field-row ' + startClass + '-row">' +
        '<div class="base-field"><label for="' + startId + '">Start time</label><input is="emby-input" type="time" id="' + startId + '" class="' + startClass + '" /></div>' +
        '<div class="base-field"><label for="' + endId + '">End time</label><input is="emby-input" type="time" id="' + endId + '" class="' + endClass + '" /></div>' +
    '</div>';
}

// buildRatingBlockCard
// Builds one rating block card. Its fields change the block as they are edited.
//
// Inputs
// block [object]: The rating block.
// ratings [object]: { options, valueOf } from loadRatings.
// onRemove [function]: Called when Remove is selected.
//
// Output
// [HTMLElement]: The card.
export function buildRatingBlockCard(block, ratings, onRemove) {
    var minId = uid('Min');
    var maxId = uid('Max');
    var periodId = uid('Period');
    var card = blockCard('schedule', 'Rating block',
        '<div class="selectContainer"><label class="selectLabel" for="' + minId + '">Minimum age rating</label>' +
            '<select is="emby-select" id="' + minId + '" class="lc-block-min"><option value="">No minimum</option>' + ratings.options + '</select></div>' +
        '<div class="selectContainer"><label class="selectLabel" for="' + maxId + '">Maximum age rating</label>' +
            '<select is="emby-select" id="' + maxId + '" class="lc-block-max"><option value="">No limit</option>' + ratings.options + '</select></div>' +
        '<div class="checkboxContainer"><label class="emby-checkbox-label">' +
            '<input type="checkbox" is="emby-checkbox" class="lc-block-unrated" /><span class="checkboxLabel">Include unrated</span></label></div>' +
        '<div class="checkboxContainer"><label class="emby-checkbox-label">' +
            '<input type="checkbox" is="emby-checkbox" class="lc-block-kids" /><span class="checkboxLabel">Tag as kids</span></label></div>' +
        '<div class="selectContainer"><label class="selectLabel" for="' + periodId + '">Period</label>' +
            '<select is="emby-select" id="' + periodId + '" class="lc-block-period"><option value="AllDay">All day</option><option value="Custom">Custom</option></select></div>' +
        timeFields('lc-block-start', 'lc-block-end'),
        onRemove);

    var min = card.querySelector('.lc-block-min');
    var max = card.querySelector('.lc-block-max');
    var unrated = card.querySelector('.lc-block-unrated');
    var kids = card.querySelector('.lc-block-kids');
    var period = card.querySelector('.lc-block-period');
    var times = card.querySelector('.lc-block-start-row');
    var start = card.querySelector('.lc-block-start');
    var end = card.querySelector('.lc-block-end');

    min.value = block.MinOfficialRating || '';
    max.value = block.MaxOfficialRating || '';
    unrated.checked = block.IncludeUnrated !== false;
    kids.checked = block.IsKids === true;
    period.value = block.Period || 'AllDay';
    start.value = minutesToTime(block.StartMinutes || 0);
    end.value = minutesToTime(block.EndMinutes || 0);

    function syncTimes() { times.classList.toggle('hidden', period.value !== 'Custom'); }
    syncTimes();

    // Drags the other end of the band to meet the one just changed, so the minimum never passes the maximum.
    function coerceBand(changed) {
        var mn = ratings.valueOf(min.value), mx = ratings.valueOf(max.value);
        if (mn !== null && mx !== null && mn > mx) {
            if (changed === 'min') { max.value = min.value; } else { min.value = max.value; }
        }
        block.MinOfficialRating = min.value;
        block.MaxOfficialRating = max.value;
    }

    min.addEventListener('change', function () { coerceBand('min'); });
    max.addEventListener('change', function () { coerceBand('max'); });
    unrated.addEventListener('change', function () { block.IncludeUnrated = unrated.checked; });
    kids.addEventListener('change', function () { block.IsKids = kids.checked; });
    period.addEventListener('change', function () { block.Period = period.value; syncTimes(); });
    start.addEventListener('change', function () { block.StartMinutes = timeToMinutes(start.value); });
    end.addEventListener('change', function () { block.EndMinutes = timeToMinutes(end.value); });
    return card;
}

// buildPlaceholderCard
// Builds one placeholder block card. Its fields change the block as they are edited.
//
// Inputs
// block [object]: The placeholder block.
// onRemove [function]: Called when Remove is selected.
//
// Output
// [HTMLElement]: The card.
export function buildPlaceholderCard(block, onRemove) {
    var nameId = uid('Name');
    var styleId = uid('Style');
    var mediaId = uid('Media');
    var titleId = uid('Title');
    var bodyId = uid('Body');
    var styleOptions = PLACEHOLDER_STYLES.map(function (s) {
        return '<option value="' + s.value + '">' + s.label + '</option>';
    }).join('');
    var card = blockCard('tv_off', 'Placeholder block',
        timeFields('lc-ph-start', 'lc-ph-end') +
        '<div class="inputContainer"><label class="inputLabel" for="' + nameId + '">Guide name</label>' +
            '<input is="emby-input" type="text" id="' + nameId + '" class="lc-ph-name" maxlength="' + MAX_TEXT + '" />' +
            '<div class="fieldDescription">What the guide calls this window.</div></div>' +
        '<div class="selectContainer"><label class="selectLabel" for="' + styleId + '">Style</label>' +
            '<select is="emby-select" id="' + styleId + '" class="lc-ph-style">' + styleOptions + '</select></div>' +
        '<div class="inputContainer lc-ph-media-row"><label class="inputLabel" for="' + mediaId + '">Image, GIF or video</label>' +
            '<input is="emby-input" type="text" id="' + mediaId + '" class="lc-ph-media" placeholder="/path/on/the/server/card.mp4" />' +
            '<div class="fieldDescription">A path on the server. A GIF or video loops cleanly when it is exactly 10 seconds and ends where it starts, and its sound is not played.</div></div>' +
        '<div class="inputContainer"><label class="inputLabel" for="' + titleId + '">Title</label>' +
            '<input is="emby-input" type="text" id="' + titleId + '" class="lc-ph-title" maxlength="' + MAX_TEXT + '" /></div>' +
        '<div class="inputContainer"><label class="inputLabel" for="' + bodyId + '">Message</label>' +
            '<input is="emby-input" type="text" id="' + bodyId + '" class="lc-ph-body" maxlength="' + MAX_TEXT + '" />' +
            '<div class="fieldDescription">{time} becomes the time the channel resumes and {contentName} what it resumes with. Leave the title or message empty to leave it off.</div></div>',
        onRemove);

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
    return card;
}

// renderBlocks
// Draws every rating and placeholder block of a channel into its list, or the empty sentence when it has none.
//
// Inputs
// host [HTMLElement]: The list the cards go in.
// blocks [object[]]: The channel's blocks, changed in place when one is removed.
// ratings [object]: { options, valueOf } from loadRatings.
// shared [object]: The helper bag from createShared.
export function renderBlocks(host, blocks, ratings, shared) {
    host.innerHTML = '';
    if (!blocks.length) {
        host.innerHTML = shared.emptySection('No rating blocks yet. Any rating airs at any time.');
        return;
    }
    blocks.forEach(function (block, index) {
        function remove() {
            blocks.splice(index, 1);
            renderBlocks(host, blocks, ratings, shared);
        }
        host.appendChild(block.IsPlaceholder ? buildPlaceholderCard(block, remove) : buildRatingBlockCard(block, ratings, remove));
    });
}
