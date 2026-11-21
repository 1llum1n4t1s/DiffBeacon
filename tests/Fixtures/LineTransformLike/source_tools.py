def block(data, marker):
    start = data.index(marker.encode()); brace = data.index(b'{', start)
    level = 0; index = brace; state = 'code'
    while index < len(data):
        c = data[index:index+1]; pair = data[index:index+2]
        if state == 'code':
            if pair == b'//': state = 'line'; index += 1
            elif pair == b'/*': state = 'comment'; index += 1
            elif c == b'"': state = 'string'
            elif c == b"'": state = 'char'
            elif c == b'{': level += 1
            elif c == b'}':
                level -= 1
                if level == 0:
                    end = index + 1
                    if marker.startswith('struct ') or marker == 'enum Options':
                        assert data[end:end+1] == b';'; end += 1
                    return start, end, data[start:end]
        elif state == 'line':
            if c == b'\n': state = 'code'
        elif state == 'comment':
            if pair == b'*/': state = 'code'; index += 1
        elif c == b'\\': index += 1
        elif (state == 'string' and c == b'"') or (state == 'char' and c == b"'"): state = 'code'
        index += 1
    raise ValueError(marker)
