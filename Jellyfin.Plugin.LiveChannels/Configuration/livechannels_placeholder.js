// livechannels_placeholder.js
// The rating and placeholder block editors that the Channels and Popular pages share, and the card helpers the
// Channels page uses for its sources.

import { createRepeater } from './configurationpage?name=livechannels_jpkribs_shared.js';

export var PLACEHOLDER_STYLES = [
    { value: 'Default', label: 'Default' },
    { value: 'Static', label: 'Static' },
    { value: 'Error', label: 'Error' },
    { value: 'Ocean', label: 'Ocean' },
    { value: 'NightSky', label: 'Night Sky' },
    { value: 'LavaLamp', label: 'Lava Lamp' },
    { value: 'Rain', label: 'Rain' },
    { value: 'Embers', label: 'Embers' },
    { value: 'Aurora', label: 'Aurora' },
    { value: 'Custom', label: 'Custom' }
];

export var DEFAULT_NAME = '-';
export var DEFAULT_TITLE = 'Thank you for watching!';
export var DEFAULT_BODY = 'We will resume at {time} with {contentName}.';
var MAX_TEXT = 200;

// pad2
// Pads a number under 10 with a leading zero.
//
// Inputs
// n [number]: The number to pad.
//
// Output
// [string]: The padded number.
function pad2(n) { return (n < 10 ? '0' : '') + n; }

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
// [Promise<object>]: { choices, valueOf }, where choices are { value, label } pairs and valueOf maps a name to its score.
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
// Wraps a sorted rating list as select choices and a score lookup.
//
// Inputs
// ratings [object[]]: { Name, Value } pairs.
//
// Output
// [object]: { choices, valueOf }.
export function ratingInfo(ratings) {
    return {
        choices: ratings.map(function (r) { return { value: r.Name, label: r.Name }; }),
        valueOf: function (name) {
            if (!name) return null;
            for (var i = 0; i < ratings.length; i++) { if (ratings[i].Name === name) return ratings[i].Value; }
            return null;
        }
    };
}

// textLimit
// Checks a text field against the longest text the server keeps.
//
// Inputs
// value [string]: The text.
//
// Output
// [string]: The error sentence, or nothing when it fits.
function textLimit(value) {
    return (value || '').length > MAX_TEXT ? 'Use ' + MAX_TEXT + ' characters or fewer.' : '';
}

// createBlockEditors
// Builds the Rating Blocks and Placeholder Blocks card lists for one channel. Both lists save into the one
// RatingBlocks array, rating blocks first.
//
// Inputs
// options [object]: { ratings, onChange }, where ratings comes from loadRatings.
//
// Output
// [object]: { ratingElement, placeholderElement, setValue(blocks), getValue(), isValid() }.
export function createBlockEditors(options) {
    var ratings = options.ratings;

    function changed() {
        if (options.onChange) options.onChange();
    }

    function customPeriod(item) { return item.Period === 'Custom'; }

    var ratingList = createRepeater({
        layout: 'card',
        titleText: 'Rating Block {index}',
        addText: 'Add Rating Block',
        emptyText: 'No rating blocks yet. Any rating airs at any time.',
        fields: [
            { key: 'MinOfficialRating', label: 'Minimum Age Rating', type: 'select', options: [{ value: '', label: 'No Minimum' }].concat(ratings.choices) },
            {
                key: 'MaxOfficialRating', label: 'Maximum Age Rating', type: 'select', options: [{ value: '', label: 'No Limit' }].concat(ratings.choices),
                validate: function (value, item) {
                    var mn = ratings.valueOf(item.MinOfficialRating), mx = ratings.valueOf(value);
                    return mn !== null && mx !== null && mn > mx ? 'Pick a maximum at or above the minimum.' : '';
                }
            },
            { key: 'IncludeUnrated', label: 'Include Unrated', type: 'toggle' },
            { key: 'IsKids', label: 'Tag As Kids', type: 'toggle' },
            { key: 'Period', label: 'Period', type: 'select', options: [{ value: 'AllDay', label: 'All Day' }, { value: 'Custom', label: 'Custom' }] },
            { key: 'StartMinutes', label: 'Start Time', type: 'time', visible: customPeriod },
            { key: 'EndMinutes', label: 'End Time', type: 'time', visible: customPeriod }
        ],
        newItem: function () { return toRatingItem({}); },
        onChange: changed
    });

    var placeholderList = createRepeater({
        layout: 'card',
        titleText: 'Placeholder Block {index}',
        addText: 'Add Placeholder Block',
        emptyText: 'No placeholder blocks yet.',
        fields: [
            { key: 'StartMinutes', label: 'Start Time', type: 'time' },
            { key: 'EndMinutes', label: 'End Time', type: 'time' },
            { key: 'PlaceholderName', label: 'Guide Name', placeholder: DEFAULT_NAME, validate: textLimit },
            { key: 'PlaceholderStyle', label: 'Style', type: 'select', options: PLACEHOLDER_STYLES },
            {
                key: 'PlaceholderMediaPath', label: 'Image, GIF or Video', placeholder: '/path/on/the/server/card.mp4',
                visible: function (item) { return item.PlaceholderStyle === 'Custom'; }
            },
            { key: 'PlaceholderTitle', label: 'Title', validate: textLimit },
            { key: 'PlaceholderBody', label: 'Message', validate: textLimit }
        ],
        newItem: function () { return toPlaceholderItem({}); },
        onChange: changed
    });

    function toRatingItem(b) {
        return {
            MinOfficialRating: b.MinOfficialRating || '',
            MaxOfficialRating: b.MaxOfficialRating || '',
            IncludeUnrated: b.IncludeUnrated !== false,
            IsKids: b.IsKids === true,
            Period: b.Period || 'AllDay',
            StartMinutes: minutesToTime(b.StartMinutes || 0),
            EndMinutes: minutesToTime(b.EndMinutes || 0)
        };
    }

    function toPlaceholderItem(b) {
        return {
            StartMinutes: minutesToTime(b.StartMinutes || 0),
            EndMinutes: minutesToTime(b.EndMinutes === undefined ? 360 : b.EndMinutes),
            PlaceholderName: b.PlaceholderName == null ? DEFAULT_NAME : b.PlaceholderName,
            PlaceholderStyle: b.PlaceholderStyle || 'Default',
            PlaceholderMediaPath: b.PlaceholderMediaPath || '',
            PlaceholderTitle: b.PlaceholderTitle == null ? DEFAULT_TITLE : b.PlaceholderTitle,
            PlaceholderBody: b.PlaceholderBody == null ? DEFAULT_BODY : b.PlaceholderBody
        };
    }

    return {
        ratingElement: ratingList.element,
        placeholderElement: placeholderList.element,
        setValue: function (blocks) {
            blocks = blocks || [];
            ratingList.setValue(blocks.filter(function (b) { return !b.IsPlaceholder; }).map(toRatingItem));
            placeholderList.setValue(blocks.filter(function (b) { return b.IsPlaceholder; }).map(toPlaceholderItem));
        },
        getValue: function () {
            var rating = ratingList.getValue().map(function (i) {
                return {
                    MinOfficialRating: i.MinOfficialRating || '',
                    MaxOfficialRating: i.MaxOfficialRating || '',
                    IncludeUnrated: !!i.IncludeUnrated,
                    IsKids: !!i.IsKids,
                    Period: i.Period || 'AllDay',
                    StartMinutes: timeToMinutes(i.StartMinutes),
                    EndMinutes: timeToMinutes(i.EndMinutes)
                };
            });
            var placeholder = placeholderList.getValue().map(function (i) {
                return {
                    IsPlaceholder: true,
                    MinOfficialRating: '',
                    MaxOfficialRating: '',
                    IncludeUnrated: true,
                    IsKids: false,
                    Period: 'Custom',
                    StartMinutes: timeToMinutes(i.StartMinutes),
                    EndMinutes: timeToMinutes(i.EndMinutes),
                    PlaceholderName: (i.PlaceholderName || '').trim() ? i.PlaceholderName : DEFAULT_NAME,
                    PlaceholderStyle: i.PlaceholderStyle || 'Default',
                    PlaceholderTitle: i.PlaceholderTitle || '',
                    PlaceholderBody: i.PlaceholderBody || '',
                    PlaceholderMediaPath: (i.PlaceholderMediaPath || '').trim()
                };
            });
            return rating.concat(placeholder);
        },
        isValid: function () { return ratingList.isValid() && placeholderList.isValid(); }
    };
}
