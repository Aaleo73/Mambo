-- Mambo owns the playlist; mpv and the user's scripts own the player UI.
-- The playlist and file options arrive over private IPC and stay in memory.
local root = 'user-data/mambo-playlist/'
local entries = {}

mp.add_hook('on_load', 50, function()
    local position = mp.get_property_number('playlist-playing-pos', -1)
    local id = mp.get_property_number('playlist/' .. position .. '/id', -1)
    local options = entries[id]
    if not options then return end

    for name, value in pairs(options) do
        local property = 'file-local-options/' .. name
        local ok
        if name == 'http-header-fields' and value == '' then
            ok = mp.set_property_native(property, {})
        else
            ok = mp.set_property(property, value)
        end
        if not ok then
            -- Never open a stream with partially applied authentication options.
            mp.commandv('stop')
            return
        end
    end
end)

mp.observe_property(root .. 'request', 'native', function(_, request)
    if type(request) ~= 'table' or type(request.sequence) ~= 'number' then return end
    mp.set_property_native(root .. 'request', {})

    -- This callback runs to completion before the on_load hook is dispatched,
    -- so even an immediate start or native next sees its own options by id.
    local result = mp.command_native({'loadlist', request.playlist, request.mode})
    local id = type(result) == 'table' and result.playlist_entry_id
    if type(id) == 'number' and id >= 0 and result.num_entries == 1 then
        if request.mode == 'replace' then entries = {} end
        entries[id] = request.options
    else
        id = -1
    end
    mp.set_property_native(root .. 'response', {sequence = request.sequence, playlist_entry_id = id})
end)

mp.set_property_native(root .. 'ready', true)
