import { StoredMap } from './stored-map';

describe('StoredMap', () => {
  it('keeps values in session storage so a new instance reads them back', () => {
    new StoredMap('test_stored_map').set('a', '1').set('b', '2');

    // 同一标签页刷新相当于重新建一个实例
    const reloaded = new StoredMap('test_stored_map');
    expect(reloaded.get('a')).toBe('1');
    expect(reloaded.has('b')).toBe(true);
  });

  it('deletes one key and clears the rest', () => {
    const map = new StoredMap('test_stored_map').set('a', '1').set('b', '2');

    expect(map.delete('a')).toBe(true);
    expect(map.delete('a')).toBe(false);
    expect(new StoredMap('test_stored_map').get('a')).toBeUndefined();

    map.clear();
    expect(new StoredMap('test_stored_map').has('b')).toBe(false);
  });
});
