const test = require('node:test');
const assert = require('node:assert/strict');
const { mergeObservedGift } = require('../src/tiktok/observed-gift');

const rose = { giftId: '5655', giftName: 'Rose', diamondCount: 1,
    giftPictureUrl: 'https://example.com/rose.png', lastSeenAt: 1 };

test('incomplete gifts preserve known catalog metadata', () => {
    for (const giftName of ['Gift', ' gift ', '', undefined]) {
        const learned = mergeObservedGift(rose, { giftId: '5655', giftName, diamondCount: 0 }, 2);
        assert.deepEqual(learned, { ...rose, lastSeenAt: 2 });
    }
});

test('real gift metadata replaces cached values and stores unit price', () => {
    const learned = mergeObservedGift(rose, { giftId: '5655', giftName: 'Hoa hồng',
        diamondCount: 15, repeatCount: 3, giftPictureUrl: 'https://example.com/new.png' }, 3);
    assert.equal(learned.giftName, 'Hoa hồng');
    assert.equal(learned.diamondCount, 5);
    assert.equal(learned.giftPictureUrl, 'https://example.com/new.png');
    assert.equal(learned.lastSeenAt, 3);
});

test('unknown gifts remain identifiable without inventing missing metadata', () => {
    assert.deepEqual(mergeObservedGift({}, { giftId: '123' }, 4), {
        giftId: '123', giftName: 'Gift', diamondCount: 0, giftPictureUrl: '', lastSeenAt: 4
    });
});
