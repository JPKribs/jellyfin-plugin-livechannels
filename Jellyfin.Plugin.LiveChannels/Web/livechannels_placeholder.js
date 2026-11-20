// livechannels_placeholder.js
// The rating and placeholder block editors that the Channels and Popular pages share, the card helpers the
// Channels page uses for its sources, and the translated text every page gives Base's components.

import { createRepeater } from './configurationpage?name=livechannels_jpkribs_shared.js';

// The address each page loads its strings from, for loadTranslations.
export var TRANSLATIONS_URL = 'livechannels/Translations';

// The placeholder card styles, each with the key of its name.
var PLACEHOLDER_STYLES = [
    { value: 'Default', key: 'Default' },
    { value: 'Static', key: 'OptionStyleStatic' },
    { value: 'Error', key: 'HeaderError' },
    { value: 'Ocean', key: 'OptionStyleOcean' },
    { value: 'NightSky', key: 'OptionStyleNightSky' },
    { value: 'LavaLamp', key: 'OptionStyleLavaLamp' },
    { value: 'Rain', key: 'OptionStyleRain' },
    { value: 'Embers', key: 'OptionStyleEmbers' },
    { value: 'Aurora', key: 'OptionStyleAurora' },
    { value: 'Custom', key: 'Custom' }
];

export var DEFAULT_NAME = '-';
var MAX_TEXT = 200;

// tabList
// Names the four tabs every page shows, in the viewer's language once the strings have loaded.
//
// Inputs
// t [object]: The translations handle.
//
// Output
// [object[]]: { href, name } for each tab.
export function tabList(t) {
    return [
        { href: 'configurationpage?name=livechannels_channels', name: t.tr('Channels', 'Channels') },
        { href: 'configurationpage?name=livechannels_popular', name: t.tr('TabPopular', 'Popular') },
        { href: 'configurationpage?name=livechannels_sessions', name: t.tr('HeaderSessions', 'Sessions') },
        { href: 'configurationpage?name=livechannels_settings', name: t.tr('Settings', 'Settings') }
    ];
}

// saveBarText
// Gives a Save Bar the line it shows after a save, which says Live TV is refreshing.
//
// Inputs
// t [object]: The translations handle.
//
// Output
// [object]: The Save Bar text options.
export function saveBarText(t) {
    return {
        savedText: t.tr('MessageSavedRefreshing')
    };
}

// withText
// Copies a component's options and adds text options to them.
//
// Inputs
// options [object]: The component's own options.
// text [object]: The text options to add.
//
// Output
// [object]: The combined options.
export function withText(options, text) {
    var out = {};
    Object.keys(text).forEach(function (k) { out[k] = text[k]; });
    Object.keys(options).forEach(function (k) { out[k] = options[k]; });
    return out;
}

// readError
// Reads the reason a failed request gives, such as a configuration save the server refused.
//
// Inputs
// err [object]: The failed request.
// fallback [string]: The text when the server gives no reason.
//
// Output
// [Promise<string>]: The reason.
export function readError(err, fallback) {
    var read = (err && err.responseText) ? Promise.resolve(err.responseText) : (err && err.text ? err.text() : Promise.resolve(''));
    return read.then(function (text) { return text ? text.replace(/^"|"$/g, '') : fallback; }, function () { return fallback; });
}

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
// Builds the check of a text field against the longest text the server keeps.
//
// Inputs
// t [object]: The translations handle.
//
// Output
// [function]: Checks a value and gives the error sentence, or nothing when it fits.
function textLimit(t) {
    return function (value) {
        return (value || '').length > MAX_TEXT ? t.format('ErrorTextTooLong', MAX_TEXT) : '';
    };
}

// createBlockEditors
// Builds the Age ratings and Placeholder blocks card lists for one channel. Both lists save into the one
// RatingBlocks array, rating blocks first.
//
// Inputs
// options [object]: { ratings, t, onChange }, where ratings comes from loadRatings and t is the translations handle.
//
// Output
// [object]: { ratingElement, placeholderElement, setValue(blocks), getValue(), isValid() }.
export function createBlockEditors(options) {
    var ratings = options.ratings;
    var t = options.t;
    var defaultTitle = t.tr('PlaceholderDefaultTitle');
    var defaultBody = t.tr('PlaceholderDefaultMessage');
    var limit = textLimit(t);

    function changed() {
        if (options.onChange) options.onChange();
    }

    function customPeriod(item) { return item.Period === 'Custom'; }

    var ratingList = createRepeater({
        layout: 'card',
        titleText: t.format('TitleRatingBlock', '{index}'),
        addText: t.tr('ButtonAddRatingBlock'),
        emptyText: t.tr('EmptyRatingBlocks'),
        fields: [
            { key: 'MinOfficialRating', label: t.tr('LabelMinAgeRating'), type: 'select', options: [{ value: '', label: t.tr('OptionNoMinimum') }].concat(ratings.choices) },
            {
                key: 'MaxOfficialRating', label: t.tr('LabelMaxAgeRating'), type: 'select', options: [{ value: '', label: t.tr('OptionNoLimit') }].concat(ratings.choices),
                validate: function (value, item) {
                    var mn = ratings.valueOf(item.MinOfficialRating), mx = ratings.valueOf(value);
                    return mn !== null && mx !== null && mn > mx ? t.tr('ErrorMaxBelowMin') : '';
                }
            },
            { key: 'IncludeUnrated', label: t.tr('LabelIncludeUnrated'), type: 'toggle' },
            { key: 'IsKids', label: t.tr('LabelTagAsKids'), type: 'toggle' },
            { key: 'Period', label: t.tr('LabelPeriod'), type: 'select', options: [{ value: 'AllDay', label: t.tr('OptionAllDay') }, { value: 'Custom', label: t.tr('Custom') }] },
            { key: 'StartMinutes', label: t.tr('LabelAccessStart'), type: 'time', visible: customPeriod },
            { key: 'EndMinutes', label: t.tr('LabelAccessEnd'), type: 'time', visible: customPeriod }
        ],
        newItem: function () { return toRatingItem({}); },
        onChange: changed
    });

    var placeholderList = createRepeater({
        layout: 'card',
        titleText: t.format('TitlePlaceholderBlock', '{index}'),
        addText: t.tr('ButtonAddPlaceholderBlock'),
        emptyText: t.tr('EmptyPlaceholderBlocks'),
        fields: [
            { key: 'StartMinutes', label: t.tr('LabelAccessStart'), type: 'time' },
            { key: 'EndMinutes', label: t.tr('LabelAccessEnd'), type: 'time' },
            { key: 'PlaceholderName', label: t.tr('LabelGuideName'), placeholder: DEFAULT_NAME, validate: limit },
            { key: 'PlaceholderStyle', label: t.tr('LabelStyle'), type: 'select', options: PLACEHOLDER_STYLES.map(function (o) { return { value: o.value, label: t.tr(o.key) }; }) },
            {
                key: 'PlaceholderMediaPath', label: t.tr('LabelPlaceholderMedia'), placeholder: '/path/on/the/server/card.mp4',
                visible: function (item) { return item.PlaceholderStyle === 'Custom'; }
            },
            { key: 'PlaceholderTitle', label: t.tr('LabelTitle'), validate: limit },
            { key: 'PlaceholderBody', label: t.tr('LabelMessage'), validate: limit }
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
            PlaceholderTitle: b.PlaceholderTitle == null ? defaultTitle : b.PlaceholderTitle,
            PlaceholderBody: b.PlaceholderBody == null ? defaultBody : b.PlaceholderBody
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
