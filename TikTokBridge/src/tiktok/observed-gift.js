function mergeObservedGift(previous, event, now = Date.now()) {
    const incomingName = String(event.giftName || '').trim();
    const giftName = incomingName && incomingName.toLowerCase() !== 'gift'
        ? incomingName
        : previous.giftName || 'Gift';
    const repeats = Math.max(1, Number(event.repeatCount) || 1);
    const unitDiamonds = Math.max(0,
        Number(event.unitDiamondCount) || Math.round((Number(event.diamondCount) || 0) / repeats));
    return {
        ...previous,
        giftId: String(event.giftId || previous.giftId || '').trim(),
        giftName,
        diamondCount: unitDiamonds || Number(previous.diamondCount) || 0,
        giftPictureUrl: String(event.giftPictureUrl || '').trim() || previous.giftPictureUrl || '',
        lastSeenAt: now
    };
}

module.exports = { mergeObservedGift };
